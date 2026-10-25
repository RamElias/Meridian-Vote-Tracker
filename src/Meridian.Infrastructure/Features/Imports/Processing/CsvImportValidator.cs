using Meridian.Infrastructure.BackgroundJobs;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Features.Imports.Processing;

public sealed class CsvImportValidator(NpgsqlDataSource dataSource, JobProgressReporter progress)
{
    private const int ValidationCommandTimeoutSeconds = 120;
    private const int LargeImportCommandTimeoutSeconds = 600;
    private sealed record MembershipPeriod(DateOnly From, DateOnly? To);

    public async Task ValidateAndPromoteAsync(Guid importId, Guid jobId, CancellationToken cancellationToken)
    {
        await using (var count = dataSource.CreateCommand("SELECT rows_total FROM imports WHERE id=@id"))
        {
            count.Parameters.AddWithValue("id",importId);
            var total = await count.ExecuteScalarAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Validating",0,total is long value ? value : null,cancellationToken,true);
        }
        var memberships = await LoadMembershipsAsync(cancellationToken);
        await MarkInvalidRowsAsync(importId, jobId, memberships, cancellationToken);
        await MarkDuplicatesAndContradictionsAsync(importId, cancellationToken);
        await PromoteAndOpenConflictsAsync(importId, jobId, cancellationToken);
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

    private async Task MarkInvalidRowsAsync(Guid importId, Guid jobId, Dictionary<string, List<MembershipPeriod>> memberships, CancellationToken cancellationToken)
    {
        await using var source = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id,row_number,normalized_values FROM import_staging_rows WHERE import_id=@id ORDER BY row_number", source);
        command.Parameters.AddWithValue("id", importId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>(1000);
        var reasons = new List<string>(1000);
        long validated = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            validated++;
            if (validated % 2000 == 0) await progress.ReportAsync(jobId,"Validating",validated,null,cancellationToken);
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
        await progress.ReportAsync(jobId,"Validating",validated,validated,cancellationToken,true);
    }

    private static string? Validate(JsonElement raw, Dictionary<string, List<MembershipPeriod>> memberships)
    {
        if (raw.TryGetProperty("__csv_error", out var parseError)) return parseError.GetString();
        if (!raw.TryGetProperty("__field_count_valid", out var shape) || !shape.GetBoolean()) return "CSV row has a different number of fields than the header.";
        string Value(string key) => raw.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
        foreach (var field in new[] { "assembly_vote_id", "session", "vote_date", "resolution_title", "country_code", "country_name", "ballot", "published_at" })
            if (string.IsNullOrWhiteSpace(Value(field))) return $"Required field '{field}' is empty.";
        if (Value("assembly_vote_id").Length > 100) return "assembly_vote_id is longer than 100 characters.";
        if (!int.TryParse(Value("session"), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return "session must be an integer.";
        if (!DateOnly.TryParseExact(Value("vote_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var voteDate)) return "vote_date must use yyyy-MM-dd format.";
        var code = Value("country_code");
        if (code.Length != 3 || code.Any(ch => ch is < 'A' or > 'Z')) return "country_code must be an uppercase ISO alpha-3 code.";
        if (!memberships.TryGetValue(code, out var periods)) return $"Unknown country_code '{code}'.";
        if (Value("ballot") is not ("Y" or "N" or "A" or "X")) return "ballot must be one of Y, N, A, or X.";
        if (!Meridian.Infrastructure.Validation.SourceTimestamp.IsValid(Value("published_at"))) return "published_at must be an ISO timestamp with seconds and a time zone.";
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
        const string duplicates = """
            WITH ranked AS (
            SELECT id,
            row_number,
            first_value(row_number) OVER (PARTITION BY normalized_values
            ORDER BY row_number) AS first_row,
            row_number() OVER (PARTITION BY normalized_values
            ORDER BY row_number) AS occurrence
            FROM import_staging_rows
            WHERE import_id=@id
            AND classification IS NULL)
            UPDATE import_staging_rows r
            SET classification='Duplicate',
            reason='Duplicate after normalization of row '||ranked.first_row
            FROM ranked
            WHERE r.id=ranked.id
            AND ranked.occurrence>1
            """;
        await using (var command = new NpgsqlCommand(duplicates, connection, transaction))
        {
            command.CommandTimeout = ValidationCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string voteContradictions = """
            WITH bad AS (
            SELECT normalized_values->>'assembly_vote_id' AS vote_id
            FROM import_staging_rows
            WHERE import_id=@id
            AND classification IS NULL
            GROUP BY normalized_values->>'assembly_vote_id'
            HAVING min(normalized_values->>'session')<>max(normalized_values->>'session')
            OR min(normalized_values->>'vote_date')<>max(normalized_values->>'vote_date')
            OR min(normalized_values->>'resolution_title')<>max(normalized_values->>'resolution_title'))
            UPDATE import_staging_rows r
            SET classification='Contradiction',
            reason='Vote metadata differs between rows in this CSV.'
            FROM bad
            WHERE r.import_id=@id
            AND r.classification IS NULL
            AND r.normalized_values->>'assembly_vote_id'=bad.vote_id
            """;
        await using (var command = new NpgsqlCommand(voteContradictions, connection, transaction))
        {
            command.CommandTimeout = ValidationCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string ballotContradictions = """
            WITH bad AS (
            SELECT normalized_values->>'assembly_vote_id' AS vote_id,
            normalized_values->>'country_code' AS country
            FROM import_staging_rows
            WHERE import_id=@id
            AND classification IS NULL
            GROUP BY normalized_values->>'assembly_vote_id',
            normalized_values->>'country_code'
            HAVING min(normalized_values->>'ballot')<>max(normalized_values->>'ballot'))
            UPDATE import_staging_rows r
            SET classification='Contradiction',
            reason='Country has different ballots for this vote in the same CSV.'
            FROM bad
            WHERE r.import_id=@id
            AND r.classification IS NULL
            AND r.normalized_values->>'assembly_vote_id'=bad.vote_id
            AND r.normalized_values->>'country_code'=bad.country
            """;
        await using (var command = new NpgsqlCommand(ballotContradictions, connection, transaction))
        {
            command.CommandTimeout = ValidationCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task PromoteAndOpenConflictsAsync(Guid importId, Guid jobId, CancellationToken cancellationToken)
    {
        await progress.ReportAsync(jobId,"Promoting",0,11,cancellationToken,true,"steps");
        long promotedSteps=0;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockConflicts = new NpgsqlCommand("SELECT pg_advisory_xact_lock(77492342)", connection, transaction))
            await lockConflicts.ExecuteNonQueryAsync(cancellationToken);
        await using (var voteConflicts = new NpgsqlCommand(VoteConflictSql, connection, transaction))
        {
            voteConflicts.CommandTimeout = LargeImportCommandTimeoutSeconds;
            voteConflicts.Parameters.AddWithValue("importId", importId);
            await voteConflicts.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var defer = new NpgsqlCommand("""
            UPDATE import_staging_rows s
            SET classification='Conflict',
            reason='Membership validation depends on the pending vote-date decision.'
            FROM votes v
            JOIN vote_versions approved ON approved.id=v.current_version_id
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            AND v.assembly_vote_id=s.normalized_values->>'assembly_vote_id'
            AND EXISTS (
            SELECT 1
            FROM conflicts pending
            CROSS JOIN LATERAL jsonb_array_elements(pending.proposals) proposal
            WHERE pending.assembly_vote_id=v.assembly_vote_id
            AND pending.target_type='VoteMetadata'
            AND pending.status='Pending'
            AND (proposal->'value'->>'voteDate')::date IS DISTINCT
            FROM approved.vote_date
            AND (EXISTS(
            SELECT 1
            FROM membership_periods m
            WHERE m.country_code=(s.normalized_values->>'country_code')::char(3)
            AND m.member_from<=approved.vote_date
            AND (m.member_to IS NULL
            OR m.member_to>=approved.vote_date))) IS DISTINCT
            FROM (EXISTS(
            SELECT 1
            FROM membership_periods m
            WHERE m.country_code=(s.normalized_values->>'country_code')::char(3)
            AND m.member_from<=(proposal->'value'->>'voteDate')::date
            AND (m.member_to IS NULL
            OR m.member_to>=(proposal->'value'->>'voteDate')::date))))
            """, connection, transaction))
        {
            defer.CommandTimeout = LargeImportCommandTimeoutSeconds;
            defer.Parameters.AddWithValue("importId", importId);
            await defer.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var membership = new NpgsqlCommand("""
            UPDATE import_staging_rows s
            SET classification='Invalid',
            reason='Country was not a member on the approved vote date.'
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            AND NOT EXISTS (
            SELECT 1
            FROM membership_periods p
            WHERE p.country_code=(s.normalized_values->>'country_code')::char(3)
            AND p.member_from<=COALESCE((
            SELECT approved.vote_date
            FROM votes v
            JOIN vote_versions approved ON approved.id=v.current_version_id
            WHERE v.assembly_vote_id=s.normalized_values->>'assembly_vote_id'),
            (s.normalized_values->>'vote_date')::date)
            AND (p.member_to IS NULL
            OR p.member_to>=COALESCE((
            SELECT approved.vote_date
            FROM votes v
            JOIN vote_versions approved ON approved.id=v.current_version_id
            WHERE v.assembly_vote_id=s.normalized_values->>'assembly_vote_id'),
            (s.normalized_values->>'vote_date')::date)))
            """, connection, transaction))
        {
            membership.CommandTimeout = LargeImportCommandTimeoutSeconds;
            membership.Parameters.AddWithValue("importId", importId);
            await membership.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var voteKeys = new NpgsqlCommand(InsertVoteKeysSql, connection, transaction))
        {
            voteKeys.CommandTimeout = LargeImportCommandTimeoutSeconds;
            voteKeys.Parameters.AddWithValue("importId", importId);
            await voteKeys.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var voteVersions = new NpgsqlCommand(InsertNewVoteVersionsSql, connection, transaction))
        {
            voteVersions.CommandTimeout = LargeImportCommandTimeoutSeconds;
            voteVersions.Parameters.AddWithValue("importId", importId);
            await voteVersions.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var countryConflicts = new NpgsqlCommand(CountryConflictSql, connection, transaction))
        {
            countryConflicts.CommandTimeout = LargeImportCommandTimeoutSeconds;
            countryConflicts.Parameters.AddWithValue("importId", importId);
            await countryConflicts.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var defer = new NpgsqlCommand("""
            UPDATE import_staging_rows s
            SET classification='Conflict',
            reason='Approved ballot differs; human decision required.'
            FROM country_votes c
            JOIN country_vote_versions current ON current.id=c.current_version_id
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            AND c.assembly_vote_id=s.normalized_values->>'assembly_vote_id'
            AND c.country_code=(s.normalized_values->>'country_code')::char(3)
            AND current.ballot<>s.normalized_values->>'ballot'
            """, connection, transaction))
        {
            defer.CommandTimeout = LargeImportCommandTimeoutSeconds;
            defer.Parameters.AddWithValue("importId", importId);
            await defer.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var insertKeys = new NpgsqlCommand(InsertCountryKeysSql, connection, transaction))
        {
            insertKeys.CommandTimeout = LargeImportCommandTimeoutSeconds;
            insertKeys.Parameters.AddWithValue("importId", importId);
            await insertKeys.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var insertVersions = new NpgsqlCommand(InsertCountryVersionsSql, connection, transaction))
        {
            insertVersions.CommandTimeout = LargeImportCommandTimeoutSeconds;
            insertVersions.Parameters.AddWithValue("importId", importId);
            await insertVersions.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var setOutcomes = new NpgsqlCommand(SetOutcomesSql, connection, transaction))
        {
            setOutcomes.CommandTimeout = LargeImportCommandTimeoutSeconds;
            setOutcomes.Parameters.AddWithValue("importId", importId);
            await setOutcomes.ExecuteNonQueryAsync(cancellationToken);
            await progress.ReportAsync(jobId,"Promoting",++promotedSteps,11,cancellationToken,false,"steps");
        }
        await using (var metadataOutcome = new NpgsqlCommand("""
            UPDATE import_staging_rows s
            SET ballot_classification=s.classification, metadata_conflict_id=c.id,
                classification='Conflict', reason='Vote metadata differs; human decision required.'
            FROM conflicts c
            WHERE s.import_id=@importId AND s.classification IN ('New','NoChange')
              AND c.assembly_vote_id=s.normalized_values->>'assembly_vote_id'
              AND c.target_type='VoteMetadata' AND c.status='Pending'
              AND EXISTS (
                SELECT 1 FROM jsonb_array_elements(c.proposals) p
                WHERE p->'source'->>'importId'=@importId::text
              )
            """, connection, transaction))
        {
            metadataOutcome.CommandTimeout = LargeImportCommandTimeoutSeconds;
            metadataOutcome.Parameters.AddWithValue("importId", importId);
            await metadataOutcome.ExecuteNonQueryAsync(cancellationToken);
        }
        // Approved versions, classifications, report and both completion markers commit together.
        var report = await BuildReportAsync(connection, transaction, importId, cancellationToken);
        await progress.ReportAsync(jobId,"Promoting",11,11,cancellationToken,true,"steps");
        await using (var complete = new NpgsqlCommand("""
            UPDATE imports
            SET status='Completed',
            report=@report,
            rows_processed=(
            SELECT count(*)
            FROM import_staging_rows
            WHERE import_id=@id),
            rows_total=(
            SELECT count(*)
            FROM import_staging_rows
            WHERE import_id=@id),
            completed_at=now()
            WHERE id=@id;
            UPDATE background_jobs
            SET status='Completed',
            progress_current=(
            SELECT rows_total
            FROM imports
            WHERE id=@id),
            progress_total=(
            SELECT rows_total
            FROM imports
            WHERE id=@id),
            completed_at=now(),
            error_message=NULL
            WHERE id=@jobId
            """, connection, transaction))
        {
            complete.Parameters.AddWithValue("id", importId);
            complete.Parameters.AddWithValue("jobId", jobId);
            complete.Parameters.AddWithValue("report", NpgsqlDbType.Jsonb, report);
            await complete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await progress.ReportAsync(jobId,"Completed",11,11,cancellationToken,true,"steps");
    }

    private const string VoteConflictSql = """
            WITH incoming AS (
            SELECT DISTINCT ON (s.normalized_values->>'assembly_vote_id') s.normalized_values->>'assembly_vote_id' AS vote_id,
            (s.normalized_values->>'session')::integer AS session,
            (s.normalized_values->>'vote_date')::date AS vote_date,
            s.normalized_values->>'resolution_title' AS title,
            s.normalized_values->>'published_at' AS published_at
            FROM import_staging_rows s
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            ORDER BY s.normalized_values->>'assembly_vote_id',
            s.row_number),
            changed AS (
            SELECT i.*,
            cv.session AS old_session,
            cv.vote_date AS old_date,
            cv.resolution_title AS old_title,
            cv.source_import_id AS old_source
            FROM incoming i
            JOIN votes v ON v.assembly_vote_id=i.vote_id
            JOIN vote_versions cv ON cv.id=v.current_version_id
            WHERE (i.session,
            i.vote_date,
            i.title) IS DISTINCT
            FROM (cv.session,
            cv.vote_date,
            cv.resolution_title)),
            proposals AS (
            SELECT changed.*,
            jsonb_build_object('value',
            jsonb_build_object('session',
            session,
            'voteDate',
            vote_date,
            'title',
            title,
            'publishedAt',
            published_at),
            'source',
            jsonb_build_object('importId',
            @importId)) AS proposal
            FROM changed)
            INSERT INTO conflicts(id,
            target_type,
            assembly_vote_id,
            current_value,
            proposals,
            opened_by_import_id)
            SELECT gen_random_uuid(),
            'VoteMetadata',
            vote_id,
            jsonb_build_object('session',
            old_session,
            'voteDate',
            old_date,
            'title',
            old_title,
            'sourceImportId',
            old_source),
            jsonb_build_array(proposal),
            @importId
            FROM proposals
            ON CONFLICT (assembly_vote_id)
            WHERE target_type='VoteMetadata'
            AND status='Pending'
            DO UPDATE SET proposals=CASE WHEN conflicts.proposals @> EXCLUDED.proposals THEN conflicts.proposals ELSE conflicts.proposals||EXCLUDED.proposals END
            """;

    private const string InsertVoteKeysSql = "INSERT INTO votes(assembly_vote_id) SELECT DISTINCT normalized_values->>'assembly_vote_id' FROM import_staging_rows WHERE import_id=@importId AND classification IS NULL ON CONFLICT DO NOTHING";
    private const string InsertNewVoteVersionsSql = """
            WITH incoming AS (
            SELECT DISTINCT ON (s.normalized_values->>'assembly_vote_id') s.normalized_values->>'assembly_vote_id' AS vote_id,
            (s.normalized_values->>'session')::integer AS session,
            (s.normalized_values->>'vote_date')::date AS vote_date,
            s.normalized_values->>'resolution_title' AS title,
            (s.normalized_values->>'published_at')::timestamptz AS published_at
            FROM import_staging_rows s
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            ORDER BY s.normalized_values->>'assembly_vote_id',
            s.row_number),
            versions AS (
            INSERT INTO vote_versions(assembly_vote_id,
            session,
            vote_date,
            resolution_title,
            published_at,
            source_import_id)
            SELECT i.vote_id,
            i.session,
            i.vote_date,
            i.title,
            i.published_at,
            @importId
            FROM incoming i
            JOIN votes v ON v.assembly_vote_id=i.vote_id
            WHERE v.current_version_id IS NULL
            RETURNING id,
            assembly_vote_id)
            UPDATE votes v
            SET current_version_id=versions.id
            FROM versions
            WHERE v.assembly_vote_id=versions.assembly_vote_id
            """;

    private const string CountryConflictSql = """
            WITH incoming AS (
            SELECT DISTINCT ON (s.normalized_values->>'assembly_vote_id',
            s.normalized_values->>'country_code') s.normalized_values->>'assembly_vote_id' AS vote_id,
            s.normalized_values->>'country_code' AS country,
            s.normalized_values->>'ballot' AS ballot,
            s.normalized_values->>'country_name' AS country_name,
            s.row_number
            FROM import_staging_rows s
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            ORDER BY s.normalized_values->>'assembly_vote_id',
            s.normalized_values->>'country_code',
            s.row_number),
            changed AS (
            SELECT i.*,
            old.ballot AS old_ballot,
            old.country_name AS old_name,
            old.source_import_id AS old_source
            FROM incoming i
            JOIN country_votes c ON c.assembly_vote_id=i.vote_id
            AND c.country_code=i.country::char(3)
            JOIN country_vote_versions old ON old.id=c.current_version_id
            WHERE old.ballot<>i.ballot),
            proposals AS (
            SELECT changed.*,
            jsonb_build_object('value',
            jsonb_build_object('ballot',
            ballot,
            'countryName',
            country_name),
            'source',
            jsonb_build_object('importId',
            @importId,
            'rowNumber',
            row_number)) AS proposal
            FROM changed)
            INSERT INTO conflicts(id,
            target_type,
            assembly_vote_id,
            country_code,
            current_value,
            proposals,
            opened_by_import_id)
            SELECT gen_random_uuid(),
            'CountryVote',
            vote_id,
            country,
            jsonb_build_object('ballot',
            old_ballot,
            'countryName',
            old_name,
            'sourceImportId',
            old_source),
            jsonb_build_array(proposal),
            @importId
            FROM proposals
            ON CONFLICT (assembly_vote_id,
            country_code)
            WHERE target_type='CountryVote'
            AND status='Pending'
            DO UPDATE SET proposals=CASE WHEN conflicts.proposals @> EXCLUDED.proposals THEN conflicts.proposals ELSE conflicts.proposals||EXCLUDED.proposals END
            """;

    private const string InsertCountryKeysSql = """
            INSERT INTO country_votes(assembly_vote_id,
            country_code)
            SELECT DISTINCT s.normalized_values->>'assembly_vote_id',
            s.normalized_values->>'country_code'
            FROM import_staging_rows s
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            ON CONFLICT DO NOTHING
            """;
    private const string InsertCountryVersionsSql = """
            WITH versions AS (
            INSERT INTO country_vote_versions(assembly_vote_id,
            country_code,
            ballot,
            country_name,
            source_import_id)
            SELECT DISTINCT ON (s.normalized_values->>'assembly_vote_id',
            s.normalized_values->>'country_code') s.normalized_values->>'assembly_vote_id',
            s.normalized_values->>'country_code',
            s.normalized_values->>'ballot',
            s.normalized_values->>'country_name',
            @importId
            FROM import_staging_rows s
            JOIN country_votes c ON c.assembly_vote_id=s.normalized_values->>'assembly_vote_id'
            AND c.country_code=(s.normalized_values->>'country_code')::char(3)
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            AND c.current_version_id IS NULL
            ORDER BY s.normalized_values->>'assembly_vote_id',
            s.normalized_values->>'country_code',
            s.row_number
            RETURNING id,
            assembly_vote_id,
            country_code)
            UPDATE country_votes c
            SET current_version_id=versions.id
            FROM versions
            WHERE c.assembly_vote_id=versions.assembly_vote_id
            AND c.country_code=versions.country_code
            """;
    private const string SetOutcomesSql = """
            UPDATE import_staging_rows s
            SET classification=CASE WHEN v.source_import_id=@importId THEN 'New' ELSE 'NoChange' END,
            reason=NULL
            FROM country_votes c
            JOIN country_vote_versions v ON v.id=c.current_version_id
            WHERE s.import_id=@importId
            AND s.classification IS NULL
            AND c.assembly_vote_id=s.normalized_values->>'assembly_vote_id'
            AND c.country_code=(s.normalized_values->>'country_code')::char(3)
            AND v.ballot=s.normalized_values->>'ballot'
            """;
    internal static async Task<string> BuildReportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid importId, CancellationToken cancellationToken)
    {
        var summary = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT classification,count(*) FROM import_staging_rows WHERE import_id=@id GROUP BY classification ORDER BY classification", connection, transaction))
        {
            command.CommandTimeout = LargeImportCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) summary[reader.GetString(0)] = reader.GetInt64(1);
        }
        var byVote = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("SELECT normalized_values->>'assembly_vote_id',classification,count(*) FROM import_staging_rows WHERE import_id=@id GROUP BY normalized_values->>'assembly_vote_id',classification ORDER BY normalized_values->>'assembly_vote_id'", connection, transaction))
        {
            command.CommandTimeout = LargeImportCommandTimeoutSeconds;
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
        await using (var command = new NpgsqlCommand("SELECT target_type,count(*) FROM conflicts c WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(c.proposals) proposal WHERE proposal->'source'->>'importId'=@id) GROUP BY target_type", connection, transaction))
        {
            command.CommandTimeout = LargeImportCommandTimeoutSeconds;
            command.Parameters.AddWithValue("id", importId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) conflicts[reader.GetString(0)] = reader.GetInt64(1);
        }
        long normalizedRows;
        await using (var normalized = new NpgsqlCommand("SELECT count(*) FROM import_staging_rows WHERE import_id=@id AND normalizations<>'{}'::jsonb", connection, transaction))
        {
            normalized.Parameters.AddWithValue("id", importId);
            normalizedRows = (long)(await normalized.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        return JsonSerializer.Serialize(new { normalizedRows, summary, votes = byVote, conflicts, flaggedRows = summary.Where(pair => pair.Key is "Invalid" or "Contradiction" or "Conflict").Sum(pair => pair.Value) });
    }
}
