using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure;

public sealed class CsvImportValidator(NpgsqlDataSource dataSource)
{
    private const int ValidationCommandTimeoutSeconds = 120;
    private sealed record MembershipPeriod(DateOnly From, DateOnly? To);

    public async Task<JsonDocument> ValidateAndPromoteAsync(Guid importId, CancellationToken cancellationToken)
    {
        var memberships = await LoadMembershipsAsync(cancellationToken);
        await MarkInvalidRowsAsync(importId, memberships, cancellationToken);
        await MarkDuplicatesAndContradictionsAsync(importId, cancellationToken);
        await PromoteAndOpenConflictsAsync(importId, cancellationToken);
        var report = await BuildReportAsync(importId, cancellationToken);
        return JsonDocument.Parse(report);
    }

    private async Task<Dictionary<string, List<MembershipPeriod>>> LoadMembershipsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<MembershipPeriod>>(StringComparer.Ordinal);
        await using var command = dataSource.CreateCommand("SELECT m.country_code,p.member_from,p.member_to FROM member_states m JOIN membership_periods p USING(country_code) ORDER BY m.country_code,p.member_from");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader.GetString(0).Trim();
            if (!result.TryGetValue(code, out var periods)) result[code] = periods = [];
            periods.Add(new MembershipPeriod(DateOnly.FromDateTime(reader.GetDateTime(1)), reader.IsDBNull(2) ? null : DateOnly.FromDateTime(reader.GetDateTime(2))));
        }
        return result;
    }

    private async Task MarkInvalidRowsAsync(Guid importId, Dictionary<string, List<MembershipPeriod>> memberships, CancellationToken cancellationToken)
    {
        await using var source = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id,row_number,raw_values FROM import_staging_rows WHERE import_id=@id ORDER BY row_number", source);
        command.Parameters.AddWithValue("id", importId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>(1000);
        var reasons = new List<string>(1000);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var rawDocument = reader.GetFieldValue<JsonDocument>(2);
            var error = Validate(rawDocument.RootElement, memberships);
            if (error is null) continue;
            ids.Add(reader.GetInt64(0));
            reasons.Add(error);
            if (ids.Count == 1000)
            {
                await UpdateInvalidBatchAsync(importId, ids, reasons, cancellationToken);
                ids.Clear();
                reasons.Clear();
            }
        }
        if (ids.Count > 0) await UpdateInvalidBatchAsync(importId, ids, reasons, cancellationToken);
    }

    private static string? Validate(JsonElement raw, Dictionary<string, List<MembershipPeriod>> memberships)
    {
        if (!raw.TryGetProperty("__field_count_valid", out var shape) || !shape.GetBoolean()) return "CSV row has a different number of fields than the header.";
        string Value(string key) => raw.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()?.Trim() ?? "" : "";
        foreach (var field in new[] { "assembly_vote_id", "session", "vote_date", "resolution_title", "country_code", "country_name", "ballot", "published_at" })
            if (Value(field).Length == 0) return $"Required field '{field}' is empty.";
        if (Value("assembly_vote_id").Length > 100) return "assembly_vote_id is longer than 100 characters.";
        if (!int.TryParse(Value("session"), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return "session must be an integer.";
        if (!DateOnly.TryParseExact(Value("vote_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var voteDate)) return "vote_date must use yyyy-MM-dd format.";
        var code = Value("country_code");
        if (code.Length != 3 || code.Any(ch => ch is < 'A' or > 'Z')) return "country_code must be an uppercase ISO alpha-3 code.";
        if (!memberships.TryGetValue(code, out var periods)) return $"Unknown country_code '{code}'.";
        if (!"YNAX".Contains(Value("ballot"), StringComparison.Ordinal)) return "ballot must be one of Y, N, A, or X.";
        if (!DateTimeOffset.TryParse(Value("published_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) return "published_at must be an ISO timestamp.";
        if (!periods.Any(period => period.From <= voteDate && (period.To is null || period.To.Value >= voteDate))) return $"{code} was not a member on {voteDate:yyyy-MM-dd}.";
        return null;
    }

    private async Task UpdateInvalidBatchAsync(Guid importId, List<long> ids, List<string> reasons, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE import_staging_rows r SET classification='Invalid',reason=v.reason FROM unnest(@ids,@reasons) AS v(id,reason) WHERE r.id=v.id AND r.import_id=@importId");
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids.ToArray());
        command.Parameters.AddWithValue("reasons", NpgsqlDbType.Array | NpgsqlDbType.Text, reasons.ToArray());
        command.Parameters.AddWithValue("importId", importId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkDuplicatesAndContradictionsAsync(Guid importId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string duplicates = "WITH ranked AS (SELECT id,row_number,first_value(row_number) OVER (PARTITION BY raw_values ORDER BY row_number) AS first_row, row_number() OVER (PARTITION BY raw_values ORDER BY row_number) AS occurrence FROM import_staging_rows WHERE import_id=@id AND classification IS NULL) UPDATE import_staging_rows r SET classification='Duplicate',reason='Exact duplicate of row '||ranked.first_row FROM ranked WHERE r.id=ranked.id AND ranked.occurrence>1";
        await using (var command = new NpgsqlCommand(duplicates, connection, transaction))
        {
            command.CommandTimeout = ValidationCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string voteContradictions = "WITH bad AS (SELECT raw_values->>'assembly_vote_id' AS vote_id FROM import_staging_rows WHERE import_id=@id AND classification IS NULL GROUP BY raw_values->>'assembly_vote_id' HAVING min(raw_values->>'session')<>max(raw_values->>'session') OR min(raw_values->>'vote_date')<>max(raw_values->>'vote_date') OR min(raw_values->>'resolution_title')<>max(raw_values->>'resolution_title')) UPDATE import_staging_rows r SET classification='Contradiction',reason='Vote metadata differs between rows in this CSV.' FROM bad WHERE r.import_id=@id AND r.classification IS NULL AND r.raw_values->>'assembly_vote_id'=bad.vote_id";
        await using (var command = new NpgsqlCommand(voteContradictions, connection, transaction))
        {
            command.CommandTimeout = ValidationCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string ballotContradictions = "WITH bad AS (SELECT raw_values->>'assembly_vote_id' AS vote_id,raw_values->>'country_code' AS country FROM import_staging_rows WHERE import_id=@id AND classification IS NULL GROUP BY raw_values->>'assembly_vote_id',raw_values->>'country_code' HAVING min(raw_values->>'ballot')<>max(raw_values->>'ballot')) UPDATE import_staging_rows r SET classification='Contradiction',reason='Country has different ballots for this vote in the same CSV.' FROM bad WHERE r.import_id=@id AND r.classification IS NULL AND r.raw_values->>'assembly_vote_id'=bad.vote_id AND r.raw_values->>'country_code'=bad.country";
        await using (var command = new NpgsqlCommand(ballotContradictions, connection, transaction))
        {
            command.CommandTimeout = ValidationCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task PromoteAndOpenConflictsAsync(Guid importId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockConflicts = new NpgsqlCommand("SELECT pg_advisory_xact_lock(77492342)", connection, transaction))
            await lockConflicts.ExecuteNonQueryAsync(cancellationToken);
        await using (var voteConflicts = new NpgsqlCommand(VoteConflictSql, connection, transaction))
        {
            voteConflicts.Parameters.AddWithValue("importId", importId);
            await voteConflicts.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var defer = new NpgsqlCommand("UPDATE import_staging_rows s SET classification='Conflict',reason='Membership validation depends on the pending vote-date decision.' FROM votes v JOIN vote_versions approved ON approved.id=v.current_version_id WHERE s.import_id=@importId AND s.classification IS NULL AND v.assembly_vote_id=s.raw_values->>'assembly_vote_id' AND approved.vote_date IS DISTINCT FROM (s.raw_values->>'vote_date')::date AND NOT EXISTS (SELECT 1 FROM membership_periods p WHERE p.country_code=(s.raw_values->>'country_code')::char(3) AND p.member_from<=approved.vote_date AND (p.member_to IS NULL OR p.member_to>=approved.vote_date))", connection, transaction))
        {
            defer.Parameters.AddWithValue("importId", importId);
            await defer.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var voteKeys = new NpgsqlCommand(InsertVoteKeysSql, connection, transaction))
        {
            voteKeys.Parameters.AddWithValue("importId", importId);
            await voteKeys.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var voteVersions = new NpgsqlCommand(InsertNewVoteVersionsSql, connection, transaction))
        {
            voteVersions.Parameters.AddWithValue("importId", importId);
            await voteVersions.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var countryConflicts = new NpgsqlCommand(CountryConflictSql, connection, transaction))
        {
            countryConflicts.Parameters.AddWithValue("importId", importId);
            await countryConflicts.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var defer = new NpgsqlCommand("UPDATE import_staging_rows s SET classification='Conflict',reason='Approved ballot differs; human decision required.' FROM country_votes c JOIN country_vote_versions current ON current.id=c.current_version_id WHERE s.import_id=@importId AND s.classification IS NULL AND c.assembly_vote_id=s.raw_values->>'assembly_vote_id' AND c.country_code=(s.raw_values->>'country_code')::char(3) AND current.ballot<>s.raw_values->>'ballot'", connection, transaction))
        {
            defer.Parameters.AddWithValue("importId", importId);
            await defer.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insertKeys = new NpgsqlCommand(InsertCountryKeysSql, connection, transaction))
        {
            insertKeys.Parameters.AddWithValue("importId", importId);
            await insertKeys.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insertVersions = new NpgsqlCommand(InsertCountryVersionsSql, connection, transaction))
        {
            insertVersions.Parameters.AddWithValue("importId", importId);
            await insertVersions.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var setOutcomes = new NpgsqlCommand(SetOutcomesSql, connection, transaction))
        {
            setOutcomes.Parameters.AddWithValue("importId", importId);
            await setOutcomes.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private const string VoteConflictSql = "WITH incoming AS (SELECT DISTINCT ON (s.raw_values->>'assembly_vote_id') s.raw_values->>'assembly_vote_id' AS vote_id,(s.raw_values->>'session')::integer AS session,(s.raw_values->>'vote_date')::date AS vote_date,s.raw_values->>'resolution_title' AS title,s.raw_values->>'published_at' AS published_at FROM import_staging_rows s WHERE s.import_id=@importId AND s.classification IS NULL ORDER BY s.raw_values->>'assembly_vote_id',s.row_number), changed AS (SELECT i.*,cv.session AS old_session,cv.vote_date AS old_date,cv.resolution_title AS old_title,cv.source_import_id AS old_source FROM incoming i JOIN votes v ON v.assembly_vote_id=i.vote_id JOIN vote_versions cv ON cv.id=v.current_version_id WHERE (i.session,i.vote_date,i.title) IS DISTINCT FROM (cv.session,cv.vote_date,cv.resolution_title)), proposals AS (SELECT changed.*,jsonb_build_object('value',jsonb_build_object('session',session,'voteDate',vote_date,'title',title,'publishedAt',published_at),'source',jsonb_build_object('importId',@importId)) AS proposal FROM changed) INSERT INTO conflicts(id,target_type,assembly_vote_id,current_value,proposals,opened_by_import_id) SELECT gen_random_uuid(),'VoteMetadata',vote_id,jsonb_build_object('session',old_session,'voteDate',old_date,'title',old_title,'sourceImportId',old_source),jsonb_build_array(proposal),@importId FROM proposals ON CONFLICT (assembly_vote_id) WHERE target_type='VoteMetadata' AND status='Pending' DO UPDATE SET proposals=CASE WHEN conflicts.proposals @> EXCLUDED.proposals THEN conflicts.proposals ELSE conflicts.proposals||EXCLUDED.proposals END";

    private const string InsertVoteKeysSql = "INSERT INTO votes(assembly_vote_id) SELECT DISTINCT raw_values->>'assembly_vote_id' FROM import_staging_rows WHERE import_id=@importId AND classification IS NULL ON CONFLICT DO NOTHING";
    private const string InsertNewVoteVersionsSql = "WITH incoming AS (SELECT DISTINCT ON (s.raw_values->>'assembly_vote_id') s.raw_values->>'assembly_vote_id' AS vote_id,(s.raw_values->>'session')::integer AS session,(s.raw_values->>'vote_date')::date AS vote_date,s.raw_values->>'resolution_title' AS title,(s.raw_values->>'published_at')::timestamptz AS published_at FROM import_staging_rows s WHERE s.import_id=@importId AND s.classification IS NULL ORDER BY s.raw_values->>'assembly_vote_id',s.row_number), versions AS (INSERT INTO vote_versions(assembly_vote_id,session,vote_date,resolution_title,published_at,source_import_id) SELECT i.vote_id,i.session,i.vote_date,i.title,i.published_at,@importId FROM incoming i JOIN votes v ON v.assembly_vote_id=i.vote_id WHERE v.current_version_id IS NULL RETURNING id,assembly_vote_id) UPDATE votes v SET current_version_id=versions.id FROM versions WHERE v.assembly_vote_id=versions.assembly_vote_id";

    private const string CountryConflictSql = "WITH incoming AS (SELECT DISTINCT ON (s.raw_values->>'assembly_vote_id',s.raw_values->>'country_code') s.raw_values->>'assembly_vote_id' AS vote_id,s.raw_values->>'country_code' AS country,s.raw_values->>'ballot' AS ballot,s.raw_values->>'country_name' AS country_name,s.row_number FROM import_staging_rows s WHERE s.import_id=@importId AND s.classification IS NULL ORDER BY s.raw_values->>'assembly_vote_id',s.raw_values->>'country_code',s.row_number), changed AS (SELECT i.*,old.ballot AS old_ballot,old.country_name AS old_name,old.source_import_id AS old_source FROM incoming i JOIN country_votes c ON c.assembly_vote_id=i.vote_id AND c.country_code=i.country::char(3) JOIN country_vote_versions old ON old.id=c.current_version_id WHERE old.ballot<>i.ballot), proposals AS (SELECT changed.*,jsonb_build_object('value',jsonb_build_object('ballot',ballot,'countryName',country_name),'source',jsonb_build_object('importId',@importId,'rowNumber',row_number)) AS proposal FROM changed) INSERT INTO conflicts(id,target_type,assembly_vote_id,country_code,current_value,proposals,opened_by_import_id) SELECT gen_random_uuid(),'CountryVote',vote_id,country,jsonb_build_object('ballot',old_ballot,'countryName',old_name,'sourceImportId',old_source),jsonb_build_array(proposal),@importId FROM proposals ON CONFLICT (assembly_vote_id,country_code) WHERE target_type='CountryVote' AND status='Pending' DO UPDATE SET proposals=CASE WHEN conflicts.proposals @> EXCLUDED.proposals THEN conflicts.proposals ELSE conflicts.proposals||EXCLUDED.proposals END";

    private const string InsertCountryKeysSql = "INSERT INTO country_votes(assembly_vote_id,country_code) SELECT DISTINCT s.raw_values->>'assembly_vote_id',s.raw_values->>'country_code' FROM import_staging_rows s WHERE s.import_id=@importId AND s.classification IS NULL ON CONFLICT DO NOTHING";
    private const string InsertCountryVersionsSql = "WITH versions AS (INSERT INTO country_vote_versions(assembly_vote_id,country_code,ballot,country_name,source_import_id) SELECT DISTINCT ON (s.raw_values->>'assembly_vote_id',s.raw_values->>'country_code') s.raw_values->>'assembly_vote_id',s.raw_values->>'country_code',s.raw_values->>'ballot',s.raw_values->>'country_name',@importId FROM import_staging_rows s JOIN country_votes c ON c.assembly_vote_id=s.raw_values->>'assembly_vote_id' AND c.country_code=(s.raw_values->>'country_code')::char(3) WHERE s.import_id=@importId AND s.classification IS NULL AND c.current_version_id IS NULL ORDER BY s.raw_values->>'assembly_vote_id',s.raw_values->>'country_code',s.row_number RETURNING id,assembly_vote_id,country_code) UPDATE country_votes c SET current_version_id=versions.id FROM versions WHERE c.assembly_vote_id=versions.assembly_vote_id AND c.country_code=versions.country_code";
    private const string SetOutcomesSql = "UPDATE import_staging_rows s SET classification=CASE WHEN v.source_import_id=@importId THEN 'New' ELSE 'NoChange' END,reason=NULL FROM country_votes c JOIN country_vote_versions v ON v.id=c.current_version_id WHERE s.import_id=@importId AND s.classification IS NULL AND c.assembly_vote_id=s.raw_values->>'assembly_vote_id' AND c.country_code=(s.raw_values->>'country_code')::char(3) AND v.ballot=s.raw_values->>'ballot'";
    private async Task<string> BuildReportAsync(Guid importId, CancellationToken cancellationToken)
    {
        var summary = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("SELECT classification,count(*) FROM import_staging_rows WHERE import_id=@id GROUP BY classification ORDER BY classification", connection))
        {
            command.Parameters.AddWithValue("id", importId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) summary[reader.GetString(0)] = reader.GetInt64(1);
        }
        var byVote = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT raw_values->>'assembly_vote_id',classification,count(*) FROM import_staging_rows WHERE import_id=@id GROUP BY raw_values->>'assembly_vote_id',classification ORDER BY raw_values->>'assembly_vote_id'", connection))
        {
            command.Parameters.AddWithValue("id", importId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var vote = reader.IsDBNull(0) ? "(missing)" : reader.GetString(0);
                if (!byVote.TryGetValue(vote, out var counts)) byVote[vote] = counts = new Dictionary<string, long>(StringComparer.Ordinal);
                counts[reader.IsDBNull(1) ? "Unclassified" : reader.GetString(1)] = reader.GetInt64(2);
            }
        }
        var conflicts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT target_type,count(*) FROM conflicts c WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(c.proposals) proposal WHERE proposal->'source'->>'importId'=@id) GROUP BY target_type", connection))
        {
            command.Parameters.AddWithValue("id", importId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) conflicts[reader.GetString(0)] = reader.GetInt64(1);
        }
        return JsonSerializer.Serialize(new { summary, votes = byVote, conflicts, flaggedRows = summary.Where(pair => pair.Key is "Invalid" or "Contradiction" or "Conflict").Sum(pair => pair.Value) });
    }
}
