using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure;

public sealed record PositionUpdateRequest(string? Stance, int Weight, string? Note);
public sealed record PositionWriteResult(bool Updated, bool NotFound, string? Error, object? Position);

public sealed class VoteService(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<object>> ListAsync(string? search, int? session, DateOnly? fromDate, DateOnly? toDate, int offset, int limit, CancellationToken cancellationToken)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) filters.Add("v.assembly_vote_id ILIKE @search OR vv.resolution_title ILIKE @search");
        if (session.HasValue) filters.Add("vv.session=@session");
        if (fromDate.HasValue) filters.Add("vv.vote_date>=@fromDate");
        if (toDate.HasValue) filters.Add("vv.vote_date<=@toDate");
        var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters.Select(x => $"({x})"));
        var sql = $"SELECT v.assembly_vote_id,vv.session,vv.vote_date,vv.resolution_title,p.stance,p.weight,(SELECT count(*) FROM conflicts c WHERE c.assembly_vote_id=v.assembly_vote_id AND c.status='Pending') FROM votes v JOIN vote_versions vv ON vv.id=v.current_version_id LEFT JOIN vote_positions vp ON vp.assembly_vote_id=v.assembly_vote_id LEFT JOIN position_versions p ON p.id=vp.current_version_id {where} ORDER BY vv.vote_date DESC,v.assembly_vote_id LIMIT @limit OFFSET @offset";
        await using var command = dataSource.CreateCommand(sql);
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("search", $"%{search.Trim()}%");
        if (session.HasValue) command.Parameters.AddWithValue("session", session.Value);
        if (fromDate.HasValue) command.Parameters.AddWithValue("fromDate", fromDate.Value);
        if (toDate.HasValue) command.Parameters.AddWithValue("toDate", toDate.Value);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("offset", Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new { voteId = reader.GetString(0), session = reader.GetInt32(1), voteDate = reader.GetFieldValue<DateOnly>(2), title = reader.GetString(3), stance = reader.IsDBNull(4) ? null : reader.GetString(4), weight = reader.IsDBNull(5) ? (short?)null : reader.GetInt16(5), pendingConflicts = reader.GetInt64(6) });
        return result;
    }

    public async Task<object?> GetAsync(string voteId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT v.assembly_vote_id,vv.id,vv.session,vv.vote_date,vv.resolution_title,vv.published_at,p.id,p.stance,p.weight,p.actor_user_id,p.note,p.created_at,(SELECT count(*) FROM conflicts c WHERE c.assembly_vote_id=v.assembly_vote_id AND c.status='Pending') FROM votes v JOIN vote_versions vv ON vv.id=v.current_version_id LEFT JOIN vote_positions vp ON vp.assembly_vote_id=v.assembly_vote_id LEFT JOIN position_versions p ON p.id=vp.current_version_id WHERE v.assembly_vote_id=@id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", voteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = new
        {
            voteId = reader.GetString(0), voteVersionId = reader.GetInt64(1), session = reader.GetInt32(2), voteDate = reader.GetFieldValue<DateOnly>(3), title = reader.GetString(4), publishedAt = reader.GetFieldValue<DateTimeOffset>(5),
            position = reader.IsDBNull(6) ? null : new { versionId = reader.GetInt64(6), stance = reader.GetString(7), weight = reader.GetInt16(8), updatedBy = reader.GetString(9), note = reader.IsDBNull(10) ? null : reader.GetString(10), updatedAt = reader.GetFieldValue<DateTimeOffset>(11) },
            pendingConflicts = reader.GetInt64(12)
        };
        await reader.CloseAsync();
        await using var countryVoteCommand = dataSource.CreateCommand("SELECT cv.country_code,ms.country_name,cvv.ballot,cvv.id,cvv.source_import_id FROM country_votes cv JOIN member_states ms ON ms.country_code=cv.country_code JOIN country_vote_versions cvv ON cvv.id=cv.current_version_id WHERE cv.assembly_vote_id=@key ORDER BY ms.country_name,cv.country_code");
        countryVoteCommand.Parameters.AddWithValue("key", voteId);
        await using var countryVoteReader = await countryVoteCommand.ExecuteReaderAsync(cancellationToken);
        var countryVotes = new List<object>();
        while (await countryVoteReader.ReadAsync(cancellationToken))
            countryVotes.Add(new { countryCode = countryVoteReader.GetString(0).Trim(), countryName = countryVoteReader.GetString(1), ballot = countryVoteReader.GetString(2), versionId = countryVoteReader.GetInt64(3), sourceImportId = countryVoteReader.GetGuid(4) });
        await countryVoteReader.CloseAsync();
        await using var history = dataSource.CreateCommand("SELECT action,actor_user_id,old_value,new_value,note,correlation_id,occurred_at FROM audit_events WHERE (entity_key=@key AND entity_type IN ('VoteMetadata','Position','CountryVote')) OR (entity_type='CountryVote' AND entity_key LIKE @countryKey) ORDER BY occurred_at,id");
        history.Parameters.AddWithValue("key", voteId);
        history.Parameters.AddWithValue("countryKey", $"{voteId}:%");
        await using var historyReader = await history.ExecuteReaderAsync(cancellationToken);
        var events = new List<object>();
        while (await historyReader.ReadAsync(cancellationToken))
            events.Add(new { action = historyReader.GetString(0), actor = historyReader.GetString(1), oldValue = historyReader.IsDBNull(2) ? (JsonElement?)null : historyReader.GetFieldValue<JsonDocument>(2).RootElement.Clone(), newValue = historyReader.IsDBNull(3) ? (JsonElement?)null : historyReader.GetFieldValue<JsonDocument>(3).RootElement.Clone(), note = historyReader.IsDBNull(4) ? null : historyReader.GetString(4), correlationId = historyReader.IsDBNull(5) ? null : historyReader.GetString(5), occurredAt = historyReader.GetFieldValue<DateTimeOffset>(6) });
        return new { vote = result, countryVotes, history = events };
    }

    public async Task<PositionWriteResult> SetPositionAsync(string voteId, PositionUpdateRequest request, string actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        var stance = request.Stance?.Trim().ToUpperInvariant();
        if (stance is not ("Y" or "N")) return new PositionWriteResult(false, false, "Stance must be Y (For) or N (Against).", null);
        if (request.Weight is < 1 or > 5) return new PositionWriteResult(false, false, "Weight must be between 1 and 5.", null);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var vote = new NpgsqlCommand("SELECT 1 FROM votes WHERE assembly_vote_id=@id AND current_version_id IS NOT NULL FOR UPDATE", connection, transaction))
        {
            vote.Parameters.AddWithValue("id", voteId);
            if (await vote.ExecuteScalarAsync(cancellationToken) is null) return new PositionWriteResult(false, true, null, null);
        }
        await using (var ensure = new NpgsqlCommand("INSERT INTO vote_positions(assembly_vote_id) VALUES (@id) ON CONFLICT DO NOTHING", connection, transaction))
        {
            ensure.Parameters.AddWithValue("id", voteId);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }
        long? oldVersionId;
        string? oldStance;
        short? oldWeight;
        await using (var current = new NpgsqlCommand("SELECT p.id,p.stance,p.weight FROM vote_positions vp LEFT JOIN position_versions p ON p.id=vp.current_version_id WHERE vp.assembly_vote_id=@id FOR UPDATE OF vp", connection, transaction))
        {
            current.Parameters.AddWithValue("id", voteId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Position row was not created.");
            oldVersionId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            oldStance = reader.IsDBNull(1) ? null : reader.GetString(1);
            oldWeight = reader.IsDBNull(2) ? null : reader.GetInt16(2);
        }
        long newVersionId;
        await using (var insert = new NpgsqlCommand("INSERT INTO position_versions(assembly_vote_id,stance,weight,actor_user_id,note) VALUES (@id,@stance,@weight,@actor,@note) RETURNING id", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", voteId);
            insert.Parameters.AddWithValue("stance", stance);
            insert.Parameters.AddWithValue("weight", request.Weight);
            insert.Parameters.AddWithValue("actor", actorUserId);
            insert.Parameters.AddWithValue("note", (object?)request.Note?.Trim() ?? DBNull.Value);
            newVersionId = (long)(await insert.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Position version insert returned no ID."));
        }
        await using (var update = new NpgsqlCommand("UPDATE vote_positions SET current_version_id=@version WHERE assembly_vote_id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("version", newVersionId);
            update.Parameters.AddWithValue("id", voteId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var audit = new NpgsqlCommand("INSERT INTO audit_events(id,entity_type,entity_key,action,actor_user_id,old_value,new_value,note,correlation_id) VALUES (@id,'Position',@key,@action,@actor,@old,@new,@note,@correlation)", connection, transaction))
        {
            audit.Parameters.AddWithValue("id", Guid.NewGuid());
            audit.Parameters.AddWithValue("key", voteId);
            audit.Parameters.AddWithValue("action", oldVersionId.HasValue ? "PositionChanged" : "PositionSet");
            audit.Parameters.AddWithValue("actor", actorUserId);
            audit.Parameters.AddWithValue("old", NpgsqlDbType.Jsonb, oldVersionId.HasValue ? JsonSerializer.Serialize(new { versionId = oldVersionId, stance = oldStance, weight = oldWeight }) : "null");
            audit.Parameters.AddWithValue("new", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new { versionId = newVersionId, stance, weight = request.Weight }));
            audit.Parameters.AddWithValue("note", (object?)request.Note?.Trim() ?? DBNull.Value);
            audit.Parameters.AddWithValue("correlation", correlationId);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new PositionWriteResult(true, false, null, new { versionId = newVersionId, voteId, stance, weight = request.Weight, updatedBy = actorUserId });
    }
}

