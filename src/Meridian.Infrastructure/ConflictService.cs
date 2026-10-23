using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure;

public sealed record ConflictDecision(string Action, int? ProposalIndex, string? Note);
public sealed record ConflictDecisionResult(bool Applied, string? Message);

public sealed class ConflictService(NpgsqlDataSource dataSource)
{
private const string PendingSql = "SELECT c.id,c.target_type,c.assembly_vote_id,c.country_code,c.current_value,(SELECT jsonb_agg(jsonb_build_object('value',p.proposal->'value','source',jsonb_build_object('importId',i.id,'fileName',i.file_name,'uploadedBy',u.display_name,'uploadedAt',i.created_at,'rowNumber',p.proposal->'source'->'rowNumber')) ORDER BY p.ordinal) FROM jsonb_array_elements(c.proposals) WITH ORDINALITY AS p(proposal,ordinal) JOIN imports i ON i.id=(p.proposal->'source'->>'importId')::uuid LEFT JOIN app_users u ON u.user_id=i.uploaded_by_user_id) AS proposals,c.created_at,(SELECT jsonb_build_object('fileName',i.file_name,'uploadedBy',u.display_name,'uploadedAt',i.created_at) FROM imports i LEFT JOIN app_users u ON u.user_id=i.uploaded_by_user_id WHERE i.id=(c.current_value->>'sourceImportId')::uuid) AS current_source,c.status,c.decision_value,c.decided_by,c.decision_note,c.decided_at FROM conflicts c WHERE (@status='All' OR c.status=@status OR (@status='Resolved' AND c.status IN ('Accepted','Rejected'))) AND (@voteId IS NULL OR c.assembly_vote_id=@voteId) ORDER BY c.created_at DESC,c.id";

    public async Task<IReadOnlyList<object>> ListPendingAsync(string? voteId, string status, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(PendingSql);
        command.Parameters.Add("voteId", NpgsqlDbType.Text).Value = (object?)voteId ?? DBNull.Value;
        command.Parameters.AddWithValue("status", status);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
        {
            using var current = reader.GetFieldValue<JsonDocument>(4);
            using var proposals = reader.GetFieldValue<JsonDocument>(5);
            using var decision = reader.IsDBNull(9) ? null : reader.GetFieldValue<JsonDocument>(9);
            results.Add(new { id = reader.GetGuid(0), targetType = reader.GetString(1), voteId = reader.GetString(2), countryCode = reader.IsDBNull(3) ? null : reader.GetString(3).Trim(), currentValue = current.RootElement.Clone(), proposals = proposals.RootElement.Clone(), createdAt = reader.GetFieldValue<DateTimeOffset>(6), currentSource = reader.IsDBNull(7) ? (object?)null : reader.GetFieldValue<JsonDocument>(7).RootElement.Clone(), status = reader.GetString(8), decisionValue = decision?.RootElement.Clone(), decidedBy = reader.IsDBNull(10) ? null : reader.GetString(10), decisionNote = reader.IsDBNull(11) ? null : reader.GetString(11), decidedAt = reader.IsDBNull(12) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(12) });
        }
        return results;
    }

    public async Task<ConflictDecisionResult> DecideAsync(Guid conflictId, ConflictDecision decision, string actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        var accepting = string.Equals(decision.Action, "Accept", StringComparison.OrdinalIgnoreCase);
        var rejecting = string.Equals(decision.Action, "Reject", StringComparison.OrdinalIgnoreCase);
        if (!accepting && !rejecting) return new ConflictDecisionResult(false, "Action must be Accept or Reject.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var serialize = new NpgsqlCommand("SELECT pg_advisory_xact_lock(77492342)", connection, transaction))
            await serialize.ExecuteNonQueryAsync(cancellationToken);
        string targetType;
        string voteId;
        string? countryCode;
        JsonDocument current;
        JsonDocument proposals;
        Guid openedByImport;
        await using (var read = new NpgsqlCommand("SELECT target_type,assembly_vote_id,country_code,current_value,proposals,opened_by_import_id FROM conflicts WHERE id=@id AND status='Pending' FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("id", conflictId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new ConflictDecisionResult(false, "The conflict was already resolved or does not exist.");
            targetType = reader.GetString(0);
            voteId = reader.GetString(1);
            countryCode = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();
            current = reader.GetFieldValue<JsonDocument>(3);
            proposals = reader.GetFieldValue<JsonDocument>(4);
            openedByImport = reader.GetGuid(5);
        }
        using (current)
        using (proposals)
        {
            JsonElement selected = default;
            Guid selectedImport = openedByImport;
            if (accepting)
            {
                if (decision.ProposalIndex is null || decision.ProposalIndex < 0 || decision.ProposalIndex >= proposals.RootElement.GetArrayLength())
                    return new ConflictDecisionResult(false, "Choose a proposal index shown in the conflict.");
                selected = proposals.RootElement[decision.ProposalIndex.Value];
                if (Guid.TryParse(selected.GetProperty("source").GetProperty("importId").GetString(), out var sourceImport)) selectedImport = sourceImport;
            }
            if (accepting && targetType == "CountryVote")
            {
                await using var membership = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM votes v JOIN vote_versions vv ON vv.id=v.current_version_id JOIN membership_periods p ON p.country_code=@country WHERE v.assembly_vote_id=@vote AND p.member_from<=vv.vote_date AND (p.member_to IS NULL OR p.member_to>=vv.vote_date))",connection,transaction);
                membership.Parameters.AddWithValue("country",countryCode!);
                membership.Parameters.AddWithValue("vote",voteId);
                if (await membership.ExecuteScalarAsync(cancellationToken) is not true)
                    return new ConflictDecisionResult(false,"The country was not a member on the current approved vote date. Reject this proposal or review the vote date first.");
            }
            var newValue = accepting ? selected.GetProperty("value").Clone() : current.RootElement.Clone();
            if (accepting && targetType == "VoteMetadata")
                await ApplyVoteMetadataAsync(connection, transaction, voteId, selected.GetProperty("value"), selectedImport, cancellationToken);
            else if (accepting && targetType == "CountryVote" && countryCode is not null)
                await ApplyCountryVoteAsync(connection, transaction, voteId, countryCode, selected.GetProperty("value"), selectedImport, cancellationToken);
            if (targetType == "VoteMetadata")
                await RevalidateDeferredRowsAsync(connection, transaction, voteId, cancellationToken);
            var finalStatus = accepting ? "Accepted" : "Rejected";
            await using (var update = new NpgsqlCommand("UPDATE conflicts SET status=@status,decision_value=@value,decided_by=@actor,decision_note=@note,decided_at=now() WHERE id=@id AND status='Pending'", connection, transaction))
            {
                update.Parameters.AddWithValue("status", finalStatus);
                update.Parameters.AddWithValue("value", NpgsqlDbType.Jsonb, newValue.GetRawText());
                update.Parameters.AddWithValue("actor", actorUserId);
                update.Parameters.AddWithValue("note", (object?)decision.Note ?? DBNull.Value);
                update.Parameters.AddWithValue("id", conflictId);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return new ConflictDecisionResult(false, "The conflict changed while you were deciding.");
            }
            await using (var audit = new NpgsqlCommand("INSERT INTO audit_events(id,entity_type,entity_key,action,actor_user_id,old_value,new_value,source_import_id,note,correlation_id) VALUES (@id,@type,@key,@action,@actor,@old,@new,@source,@note,@correlation)", connection, transaction))
            {
                audit.Parameters.AddWithValue("id", Guid.NewGuid());
                audit.Parameters.AddWithValue("type", targetType);
                audit.Parameters.AddWithValue("key", countryCode is null ? voteId : $"{voteId}:{countryCode}");
                audit.Parameters.AddWithValue("action", accepting ? "ConflictAccepted" : "ConflictRejected");
                audit.Parameters.AddWithValue("actor", actorUserId);
                audit.Parameters.AddWithValue("old", NpgsqlDbType.Jsonb, current.RootElement.GetRawText());
                audit.Parameters.AddWithValue("new", NpgsqlDbType.Jsonb, newValue.GetRawText());
                audit.Parameters.AddWithValue("source", selectedImport);
                audit.Parameters.AddWithValue("note", (object?)decision.Note ?? DBNull.Value);
                audit.Parameters.AddWithValue("correlation", correlationId);
                await audit.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new ConflictDecisionResult(true, null);
    }

    private static async Task ApplyVoteMetadataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string voteId, JsonElement value, Guid sourceImportId, CancellationToken cancellationToken)
    {
        var session = value.GetProperty("session").GetInt32();
        var voteDate = DateOnly.ParseExact(value.GetProperty("voteDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = value.GetProperty("title").GetString()!;
        var publishedAt = DateTimeOffset.Parse(value.GetProperty("publishedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        long versionId;
        await using (var insert = new NpgsqlCommand("INSERT INTO vote_versions(assembly_vote_id,session,vote_date,resolution_title,published_at,source_import_id) VALUES (@id,@session,@date,@title,@published,@source) RETURNING id", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", voteId);
            insert.Parameters.AddWithValue("session", session);
            insert.Parameters.AddWithValue("date", voteDate);
            insert.Parameters.AddWithValue("title", title);
            insert.Parameters.AddWithValue("published", publishedAt);
            insert.Parameters.AddWithValue("source", sourceImportId);
            versionId = (long)(await insert.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Vote version insert returned no ID."));
        }
        await using var update = new NpgsqlCommand("UPDATE votes SET current_version_id=@version WHERE assembly_vote_id=@id", connection, transaction);
        update.Parameters.AddWithValue("version", versionId);
        update.Parameters.AddWithValue("id", voteId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyCountryVoteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string voteId, string countryCode, JsonElement value, Guid sourceImportId, CancellationToken cancellationToken)
    {
        var ballot = value.GetProperty("ballot").GetString()!;
        var countryName = value.GetProperty("countryName").GetString()!;
        long versionId;
        await using (var insert = new NpgsqlCommand("INSERT INTO country_vote_versions(assembly_vote_id,country_code,ballot,country_name,source_import_id) VALUES (@vote,@country,@ballot,@name,@source) RETURNING id", connection, transaction))
        {
            insert.Parameters.AddWithValue("vote", voteId);
            insert.Parameters.AddWithValue("country", countryCode);
            insert.Parameters.AddWithValue("ballot", ballot);
            insert.Parameters.AddWithValue("name", countryName);
            insert.Parameters.AddWithValue("source", sourceImportId);
            versionId = (long)(await insert.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Country ballot version insert returned no ID."));
        }
        await using var update = new NpgsqlCommand("UPDATE country_votes SET current_version_id=@version WHERE assembly_vote_id=@vote AND country_code=@country", connection, transaction);
        update.Parameters.AddWithValue("version", versionId);
        update.Parameters.AddWithValue("vote", voteId);
        update.Parameters.AddWithValue("country", countryCode);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevalidateDeferredRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string voteId, CancellationToken cancellationToken)
    {
        const string reason = "Membership validation depends on the pending vote-date decision.";
        var importIds = new List<Guid>();
        await using (var capture = new NpgsqlCommand("CREATE TEMP TABLE deferred_rows ON COMMIT DROP AS SELECT id,import_id FROM import_staging_rows WHERE normalized_values->>'assembly_vote_id'=@voteId AND classification='Conflict' AND reason=@reason; SELECT DISTINCT import_id FROM deferred_rows", connection, transaction))
        {
            capture.Parameters.AddWithValue("voteId", voteId);
            capture.Parameters.AddWithValue("reason", reason);
            await using var reader = await capture.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) importIds.Add(reader.GetGuid(0));
        }
        var statements = new[]
        {
            $"UPDATE import_staging_rows s SET classification='Invalid',reason='Country was not a member on the resolved vote date.' FROM votes v JOIN vote_versions vv ON vv.id=v.current_version_id WHERE s.id IN (SELECT id FROM deferred_rows) AND s.normalized_values->>'assembly_vote_id'=@voteId AND s.classification='Conflict' AND s.reason=@reason AND v.assembly_vote_id=@voteId AND NOT EXISTS (SELECT 1 FROM membership_periods p WHERE p.country_code=(s.normalized_values->>'country_code')::char(3) AND p.member_from<=vv.vote_date AND (p.member_to IS NULL OR p.member_to>=vv.vote_date))",
            $"UPDATE import_staging_rows s SET classification=NULL,reason=NULL FROM votes v JOIN vote_versions vv ON vv.id=v.current_version_id WHERE s.id IN (SELECT id FROM deferred_rows) AND s.normalized_values->>'assembly_vote_id'=@voteId AND s.classification='Conflict' AND s.reason=@reason AND v.assembly_vote_id=@voteId AND EXISTS (SELECT 1 FROM membership_periods p WHERE p.country_code=(s.normalized_values->>'country_code')::char(3) AND p.member_from<=vv.vote_date AND (p.member_to IS NULL OR p.member_to>=vv.vote_date))",
            "WITH incoming AS (SELECT s.import_id,s.row_number,s.normalized_values->>'assembly_vote_id' AS vote_id,s.normalized_values->>'country_code' AS country,s.normalized_values->>'ballot' AS ballot,s.normalized_values->>'country_name' AS country_name,old.ballot AS old_ballot,old.country_name AS old_name,old.source_import_id AS old_source FROM import_staging_rows s JOIN country_votes c ON c.assembly_vote_id=s.normalized_values->>'assembly_vote_id' AND c.country_code=(s.normalized_values->>'country_code')::char(3) JOIN country_vote_versions old ON old.id=c.current_version_id WHERE s.id IN (SELECT id FROM deferred_rows) AND s.normalized_values->>'assembly_vote_id'=@voteId AND s.classification IS NULL AND old.ballot<>s.normalized_values->>'ballot'), grouped AS (SELECT vote_id,country,old_ballot,old_name,old_source,jsonb_agg(jsonb_build_object('value',jsonb_build_object('ballot',ballot,'countryName',country_name),'source',jsonb_build_object('importId',import_id,'rowNumber',row_number)) ORDER BY import_id,row_number) AS proposals,min(import_id::text)::uuid AS opened_by FROM incoming GROUP BY vote_id,country,old_ballot,old_name,old_source) INSERT INTO conflicts(id,target_type,assembly_vote_id,country_code,current_value,proposals,opened_by_import_id) SELECT gen_random_uuid(),'CountryVote',vote_id,country,jsonb_build_object('ballot',old_ballot,'countryName',old_name,'sourceImportId',old_source),proposals,opened_by FROM grouped ON CONFLICT (assembly_vote_id,country_code) WHERE target_type='CountryVote' AND status='Pending' DO UPDATE SET proposals=CASE WHEN conflicts.proposals @> EXCLUDED.proposals THEN conflicts.proposals ELSE conflicts.proposals||EXCLUDED.proposals END",
            "UPDATE import_staging_rows s SET classification='Conflict',reason='Approved ballot differs; human decision required.' FROM country_votes c JOIN country_vote_versions current ON current.id=c.current_version_id WHERE s.id IN (SELECT id FROM deferred_rows) AND s.normalized_values->>'assembly_vote_id'=@voteId AND s.classification IS NULL AND c.assembly_vote_id=@voteId AND c.country_code=(s.normalized_values->>'country_code')::char(3) AND current.ballot<>s.normalized_values->>'ballot'",
            "INSERT INTO country_votes(assembly_vote_id,country_code) SELECT DISTINCT s.normalized_values->>'assembly_vote_id',(s.normalized_values->>'country_code')::char(3) FROM import_staging_rows s WHERE s.id IN (SELECT id FROM deferred_rows) AND s.normalized_values->>'assembly_vote_id'=@voteId AND s.classification IS NULL ON CONFLICT DO NOTHING",
            "WITH versions AS (INSERT INTO country_vote_versions(assembly_vote_id,country_code,ballot,country_name,source_import_id) SELECT DISTINCT ON (s.normalized_values->>'assembly_vote_id',s.normalized_values->>'country_code') s.normalized_values->>'assembly_vote_id',(s.normalized_values->>'country_code')::char(3),s.normalized_values->>'ballot',s.normalized_values->>'country_name',s.import_id FROM import_staging_rows s JOIN imports source_import ON source_import.id=s.import_id JOIN country_votes c ON c.assembly_vote_id=s.normalized_values->>'assembly_vote_id' AND c.country_code=(s.normalized_values->>'country_code')::char(3) WHERE s.id IN (SELECT id FROM deferred_rows) AND s.normalized_values->>'assembly_vote_id'=@voteId AND s.classification IS NULL AND c.current_version_id IS NULL ORDER BY s.normalized_values->>'assembly_vote_id',s.normalized_values->>'country_code',source_import.created_at,s.import_id,s.row_number RETURNING id,assembly_vote_id,country_code) UPDATE country_votes c SET current_version_id=versions.id FROM versions WHERE c.assembly_vote_id=versions.assembly_vote_id AND c.country_code=versions.country_code",
            "UPDATE import_staging_rows s SET classification=CASE WHEN v.source_import_id=s.import_id THEN 'New' ELSE 'NoChange' END,reason=NULL FROM country_votes c JOIN country_vote_versions v ON v.id=c.current_version_id WHERE s.id IN (SELECT id FROM deferred_rows) AND s.normalized_values->>'assembly_vote_id'=@voteId AND s.classification IS NULL AND c.assembly_vote_id=@voteId AND c.country_code=(s.normalized_values->>'country_code')::char(3) AND v.ballot=s.normalized_values->>'ballot'"
        };
        foreach (var sql in new[] { statements[0], statements[1], statements[4], statements[5], statements[2], statements[3], statements[6] })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("voteId", voteId);
            if (sql.Contains("@reason", StringComparison.Ordinal)) command.Parameters.AddWithValue("reason", reason);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var importId in importIds)
        {
            var report = await CsvImportValidator.BuildReportAsync(connection, transaction, importId, cancellationToken);
            await using var refresh = new NpgsqlCommand("UPDATE imports SET report=@report WHERE id=@id", connection, transaction);
            refresh.Parameters.AddWithValue("id", importId);
            refresh.Parameters.AddWithValue("report", NpgsqlDbType.Jsonb, report);
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
