using Meridian.Infrastructure.Features.Snapshots.Contracts;
using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Features.Snapshots.Services;

public sealed class SnapshotService(NpgsqlDataSource dataSource) : ISnapshotService
{
    private sealed record SelectedVote(string VoteId, long VoteVersionId, int Session, DateOnly VoteDate, string Title, long PositionVersionId, string Stance, short Weight);
    private const string RuleVersion = "alignment-v1";

    public async Task<SnapshotCreateResult> CreateAsync(SnapshotRequest request, string actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        if (!request.Session.HasValue && !request.FromDate.HasValue && !request.ToDate.HasValue && (request.VoteIds is null || request.VoteIds.Count == 0))
            return new SnapshotCreateResult(false, null, "Select at least one session, date boundary, or explicit vote ID.", null, 0);
        if (request.FromDate.HasValue && request.ToDate.HasValue && request.FromDate > request.ToDate)
            return new SnapshotCreateResult(false, null, "fromDate must be on or before toDate.", null, 0);
        var ids = request.VoteIds?.Select(id => id?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToArray();
        if (ids is { Length: > 5000 } || ids?.Any(string.IsNullOrWhiteSpace) == true)
            return new SnapshotCreateResult(false, null, "Provide at most 5,000 non-empty vote IDs.", null, 0);

        var filters = new List<string>();
        if (request.Session.HasValue) filters.Add("vv.session=@session");
        if (request.FromDate.HasValue) filters.Add("vv.vote_date>=@fromDate");
        if (request.ToDate.HasValue) filters.Add("vv.vote_date<=@toDate");
        if (ids is { Length: > 0 }) filters.Add("v.assembly_vote_id=ANY(@ids)");
        var sql = $"SELECT v.assembly_vote_id,vv.id,vv.session,vv.vote_date,vv.resolution_title,p.id,p.stance,p.weight FROM votes v JOIN vote_versions vv ON vv.id=v.current_version_id LEFT JOIN vote_positions vp ON vp.assembly_vote_id=v.assembly_vote_id LEFT JOIN position_versions p ON p.id=vp.current_version_id WHERE {string.Join(" AND ", filters)} ORDER BY vv.vote_date,v.assembly_vote_id";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (ids is { Length: > 0 })
        {
            await using var known = new NpgsqlCommand("SELECT assembly_vote_id FROM votes WHERE assembly_vote_id=ANY(@ids) AND current_version_id IS NOT NULL", connection, transaction);
            known.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Text, ids);
            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var reader = await known.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
            var unknown = ids.Where(id => !existing.Contains(id)).ToArray();
            if (unknown.Length > 0) return new SnapshotCreateResult(false, null, "Some requested vote IDs do not exist: " + string.Join(", ", unknown), null, 0, unknown);
        }
        var selected = new List<SelectedVote>();
        var missing = new List<string>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            if (request.Session.HasValue) command.Parameters.AddWithValue("session", request.Session.Value);
            if (request.FromDate.HasValue) command.Parameters.AddWithValue("fromDate", request.FromDate.Value);
            if (request.ToDate.HasValue) command.Parameters.AddWithValue("toDate", request.ToDate.Value);
            if (ids is { Length: > 0 }) command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Text, ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(5))
                {
                    if (missing.Count < 50) missing.Add(reader.GetString(0));
                    continue;
                }
                selected.Add(new SelectedVote(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetInt16(7)));
            }
        }
        if (missing.Count > 0) return new SnapshotCreateResult(false, null, "Every selected vote needs an approved institute position before it can be ranked.", missing, selected.Count + missing.Count);
        if (selected.Count == 0) return new SnapshotCreateResult(false, null, "No approved votes matched the selection.", null, 0);

        var snapshotId = Guid.NewGuid();
        var selection = JsonSerializer.Serialize(new { request.Session, request.FromDate, request.ToDate, voteIds = ids });
        await using (var job = new NpgsqlCommand("INSERT INTO background_jobs(id,job_type,status,payload) VALUES (@id,'ranking_snapshot','Queued',jsonb_build_object('snapshotId',@snapshotId,'correlationId',@correlationId))", connection, transaction))
        {
            job.Parameters.AddWithValue("id", snapshotId);
            job.Parameters.AddWithValue("snapshotId", snapshotId.ToString());
            job.Parameters.AddWithValue("correlationId", correlationId);
            await job.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var snapshot = new NpgsqlCommand("INSERT INTO ranking_snapshots(id,status,selection,calculation_rule_version,created_by_user_id) VALUES (@id,'Queued',@selection,@rule,@actor)", connection, transaction))
        {
            snapshot.Parameters.AddWithValue("id", snapshotId);
            snapshot.Parameters.AddWithValue("selection", NpgsqlDbType.Jsonb, selection);
            snapshot.Parameters.AddWithValue("rule", RuleVersion);
            snapshot.Parameters.AddWithValue("actor", actorUserId);
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var countryCount = new NpgsqlCommand("SELECT count(*) FROM member_states", connection, transaction))
        {
            var count = (long)(await countryCount.ExecuteScalarAsync(cancellationToken) ?? 0L);
            await using var total = new NpgsqlCommand("UPDATE background_jobs SET progress_total=@total WHERE id=@id", connection, transaction);
            total.Parameters.AddWithValue("total", count * selected.Count);
            total.Parameters.AddWithValue("id", snapshotId);
            await total.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var importer = await connection.BeginBinaryImportAsync("COPY ranking_snapshot_votes(snapshot_id,ordinal,assembly_vote_id,vote_version_id,position_version_id,vote_date,session,resolution_title,stance,weight) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            for (var i = 0; i < selected.Count; i++)
            {
                var vote = selected[i];
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(snapshotId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(i + 1, NpgsqlDbType.Integer, cancellationToken);
                await importer.WriteAsync(vote.VoteId, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(vote.VoteVersionId, NpgsqlDbType.Bigint, cancellationToken);
                await importer.WriteAsync(vote.PositionVersionId, NpgsqlDbType.Bigint, cancellationToken);
                await importer.WriteAsync(vote.VoteDate, NpgsqlDbType.Date, cancellationToken);
                await importer.WriteAsync(vote.Session, NpgsqlDbType.Integer, cancellationToken);
                await importer.WriteAsync(vote.Title, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(vote.Stance, NpgsqlDbType.Char, cancellationToken);
                await importer.WriteAsync(vote.Weight, NpgsqlDbType.Smallint, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SnapshotCreateResult(true, snapshotId, null, null, selected.Count);
    }

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT s.id,s.status,s.calculation_rule_version,s.created_by_user_id,s.created_at,s.completed_at,s.publication_year,s.publication_quarter,s.published_at,s.withdrawn_at,s.superseded_at,j.progress_current,j.progress_total,(SELECT count(*) FROM ranking_snapshot_votes v WHERE v.snapshot_id=s.id),(SELECT jsonb_build_object('phase',p.phase,'current',p.current_count,'total',p.total_count,'unit',p.unit,'updatedAt',p.updated_at) FROM job_progress p WHERE p.job_id=s.id) AS progress FROM ranking_snapshots s JOIN background_jobs j ON j.id=s.id ORDER BY s.created_at DESC LIMIT 200";
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new { id = reader.GetGuid(0), status = reader.GetString(1), ruleVersion = reader.GetString(2), createdBy = reader.GetString(3), createdAt = reader.GetFieldValue<DateTimeOffset>(4), completedAt = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5), publicationYear = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6), publicationQuarter = reader.IsDBNull(7) ? (short?)null : reader.GetInt16(7), publishedAt = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8), withdrawnAt = reader.IsDBNull(9) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(9), supersededAt = reader.IsDBNull(10) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(10), progressCurrent = reader.GetInt64(11), progressTotal = reader.IsDBNull(12) ? (long?)null : reader.GetInt64(12), selectedVoteCount = reader.GetInt64(13), error = (string?)null, progress = reader.IsDBNull(14) ? (JsonElement?)null : reader.GetFieldValue<JsonDocument>(14).RootElement.Clone() });
        return items;
    }

    public async Task<object?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT s.id,s.status,s.selection,s.calculation_rule_version,s.created_by_user_id,s.created_at,s.inputs_captured_at,s.completed_at,s.error_message,s.publication_year,s.publication_quarter,s.published_at,s.published_by_user_id,s.withdrawn_at,s.withdrawn_by_user_id,s.superseded_at,s.superseded_by,j.progress_current,j.progress_total,(SELECT count(*) FROM ranking_snapshot_votes v WHERE v.snapshot_id=s.id),(SELECT jsonb_build_object('phase',p.phase,'current',p.current_count,'total',p.total_count,'unit',p.unit,'updatedAt',p.updated_at) FROM job_progress p WHERE p.job_id=s.id) AS progress FROM ranking_snapshots s JOIN background_jobs j ON j.id=s.id WHERE s.id=@id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        using var selection = reader.GetFieldValue<JsonDocument>(2);
        return new { id = reader.GetGuid(0), status = reader.GetString(1), selection = selection.RootElement.Clone(), ruleVersion = reader.GetString(3), createdBy = reader.GetString(4), createdAt = reader.GetFieldValue<DateTimeOffset>(5), inputsCapturedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6), completedAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7), error = reader.IsDBNull(8) ? null : reader.GetString(8), publicationYear = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9), publicationQuarter = reader.IsDBNull(10) ? (short?)null : reader.GetInt16(10), publishedAt = reader.IsDBNull(11) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(11), publishedBy = reader.IsDBNull(12) ? null : reader.GetString(12), withdrawnAt = reader.IsDBNull(13) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(13), withdrawnBy = reader.IsDBNull(14) ? null : reader.GetString(14), supersededAt = reader.IsDBNull(15) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(15), supersededBy = reader.IsDBNull(16) ? (Guid?)null : reader.GetGuid(16), progressCurrent = reader.GetInt64(17), progressTotal = reader.IsDBNull(18) ? (long?)null : reader.GetInt64(18), selectedVoteCount = reader.GetInt64(19), progress = reader.IsDBNull(20) ? (JsonElement?)null : reader.GetFieldValue<JsonDocument>(20).RootElement.Clone() };
    }

    public async Task<IReadOnlyList<object>> ListResultsAsync(Guid id, int offset, int limit, CancellationToken cancellationToken)
    {
        const string sql = "SELECT country_code,country_name,rank,alignment_score,alignment_numerator,alignment_denominator,participation_percent,participating_ballots,approved_ballots,coverage_percent,approved_member_records,eligible_member_votes FROM ranking_snapshot_results WHERE snapshot_id=@id ORDER BY rank NULLS LAST,country_code OFFSET @offset LIMIT @limit";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("offset", Math.Max(0, offset));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new { countryCode = reader.GetString(0).Trim(), countryName = reader.GetString(1), rank = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2), alignmentScore = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3), alignmentNumerator = reader.GetDecimal(4), alignmentDenominator = reader.GetDecimal(5), participationPercent = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6), participatingBallots = reader.GetInt32(7), approvedBallots = reader.GetInt32(8), coveragePercent = reader.IsDBNull(9) ? (decimal?)null : reader.GetDecimal(9), approvedMemberRecords = reader.GetInt32(10), eligibleMemberVotes = reader.GetInt32(11) });
        return items;
    }

    public async Task<IReadOnlyList<object>> ListContributionsAsync(Guid id, string countryCode, int afterOrdinal, int limit, CancellationToken cancellationToken)
    {
        const string sql = "SELECT sv.ordinal,sv.assembly_vote_id,sv.session,sv.vote_date,sv.resolution_title,sv.stance,sv.weight,c.member_at_vote,c.ballot,c.alignment_numerator,c.alignment_denominator,c.participation_numerator,c.participation_denominator,c.coverage_numerator,c.coverage_denominator,c.country_vote_version_id FROM ranking_snapshot_contributions c JOIN ranking_snapshot_votes sv ON sv.snapshot_id=c.snapshot_id AND sv.assembly_vote_id=c.assembly_vote_id WHERE c.snapshot_id=@id AND c.country_code=@country AND sv.ordinal>@after ORDER BY sv.ordinal LIMIT @limit";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("country", countryCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("after", Math.Max(0, afterOrdinal));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new { ordinal = reader.GetInt32(0), voteId = reader.GetString(1), session = reader.GetInt32(2), voteDate = reader.GetFieldValue<DateOnly>(3), title = reader.GetString(4), instituteStance = reader.GetString(5), weight = reader.GetInt16(6), memberAtVote = reader.GetBoolean(7), ballot = reader.IsDBNull(8) ? null : reader.GetString(8), alignmentNumerator = reader.GetDecimal(9), alignmentDenominator = reader.GetDecimal(10), participationNumerator = reader.GetInt32(11), participationDenominator = reader.GetInt32(12), coverageNumerator = reader.GetInt32(13), coverageDenominator = reader.GetInt32(14), countryVoteVersionId = reader.IsDBNull(15) ? (long?)null : reader.GetInt64(15) });
        return items;
    }

    public async Task<SnapshotPublicationResult> PublishAsync(Guid id, int year, int quarter, bool replaceActive, string actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        if (year is < 1900 or > 9999 || quarter is < 1 or > 4) return new SnapshotPublicationResult(false, "Publication year or quarter is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var serialize = new NpgsqlCommand("SELECT pg_advisory_xact_lock(77492343)", connection, transaction))
            await serialize.ExecuteNonQueryAsync(cancellationToken);
        string status;
        int? oldYear;
        short? oldQuarter;
        await using (var read = new NpgsqlCommand("SELECT status,publication_year,publication_quarter FROM ranking_snapshots WHERE id=@id FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new SnapshotPublicationResult(false, null, true);
            status = reader.GetString(0);
            oldYear = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            oldQuarter = reader.IsDBNull(2) ? null : reader.GetInt16(2);
        }
        if (status != "Completed") return new SnapshotPublicationResult(false, "Only completed snapshots can be published.");
        Guid? activeId = null;
        await using (var active = new NpgsqlCommand("SELECT id FROM ranking_snapshots WHERE publication_year=@year AND publication_quarter=@quarter AND published_at IS NOT NULL AND withdrawn_at IS NULL AND superseded_at IS NULL AND id<>@id FOR UPDATE", connection, transaction))
        {
            active.Parameters.AddWithValue("year", year);
            active.Parameters.AddWithValue("quarter", quarter);
            active.Parameters.AddWithValue("id", id);
            var result = await active.ExecuteScalarAsync(cancellationToken);
            if (result is Guid existing) activeId = existing;
        }
        if (activeId.HasValue && !replaceActive) return new SnapshotPublicationResult(false, $"Snapshot {activeId} is already active for {year} Q{quarter}; set replaceActive to replace it.");
        try
        {
            if (activeId.HasValue)
            {
                await using var supersede = new NpgsqlCommand("UPDATE ranking_snapshots SET superseded_at=now(),superseded_by=@newId WHERE id=@oldId", connection, transaction);
                supersede.Parameters.AddWithValue("newId", id);
                supersede.Parameters.AddWithValue("oldId", activeId.Value);
                await supersede.ExecuteNonQueryAsync(cancellationToken);
                await InsertAuditAsync(connection, transaction, activeId.Value, "SnapshotSuperseded", actorUserId, new { publicationYear = year, publicationQuarter = quarter }, new { supersededBy = id }, correlationId, cancellationToken);
            }
            await using (var update = new NpgsqlCommand("UPDATE ranking_snapshots SET publication_year=@year,publication_quarter=@quarter,published_at=now(),published_by_user_id=@actor,withdrawn_at=NULL,withdrawn_by_user_id=NULL,superseded_at=NULL,superseded_by=NULL WHERE id=@id", connection, transaction))
            {
                update.Parameters.AddWithValue("year", year);
                update.Parameters.AddWithValue("quarter", quarter);
                update.Parameters.AddWithValue("actor", actorUserId);
                update.Parameters.AddWithValue("id", id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertAuditAsync(connection, transaction, id, "SnapshotPublished", actorUserId, new { publicationYear = oldYear, publicationQuarter = oldQuarter }, new { publicationYear = year, publicationQuarter = quarter }, correlationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SnapshotPublicationResult(true, null);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == "ux_active_snapshot_publication_period")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new SnapshotPublicationResult(false, $"Another snapshot was published for {year} Q{quarter} at the same time. Refresh and try again.");
        }
    }

    public async Task<SnapshotPublicationResult> WithdrawAsync(Guid id, string actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var serialize = new NpgsqlCommand("SELECT pg_advisory_xact_lock(77492343)", connection, transaction))
            await serialize.ExecuteNonQueryAsync(cancellationToken);
        int year;
        short quarter;
        await using (var read = new NpgsqlCommand("SELECT publication_year,publication_quarter FROM ranking_snapshots WHERE id=@id AND published_at IS NOT NULL AND withdrawn_at IS NULL AND superseded_at IS NULL FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new SnapshotPublicationResult(false, "Snapshot is not actively published.", true);
            year = reader.GetInt32(0);
            quarter = reader.GetInt16(1);
        }
        await using (var update = new NpgsqlCommand("UPDATE ranking_snapshots SET withdrawn_at=now(),withdrawn_by_user_id=@actor WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("actor", actorUserId);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertAuditAsync(connection, transaction, id, "SnapshotWithdrawn", actorUserId, new { publicationYear = year, publicationQuarter = quarter }, null, correlationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SnapshotPublicationResult(true, null);
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid snapshotId, string action, string actor, object? oldValue, object? newValue, string correlationId, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("INSERT INTO audit_events(id,entity_type,entity_key,action,actor_user_id,old_value,new_value,correlation_id) VALUES (@id,'SnapshotPublication',@key,@action,@actor,@old,@new,@correlation)", connection, transaction);
        audit.Parameters.AddWithValue("id", Guid.NewGuid());
        audit.Parameters.AddWithValue("key", snapshotId.ToString());
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("actor", actor);
        audit.Parameters.AddWithValue("old", NpgsqlDbType.Jsonb, oldValue is null ? "null" : JsonSerializer.Serialize(oldValue));
        audit.Parameters.AddWithValue("new", NpgsqlDbType.Jsonb, newValue is null ? "null" : JsonSerializer.Serialize(newValue));
        audit.Parameters.AddWithValue("correlation", correlationId);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}

