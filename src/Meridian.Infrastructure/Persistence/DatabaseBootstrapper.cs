using Microsoft.Extensions.Logging;
using Npgsql;

namespace Meridian.Infrastructure.Persistence;

public sealed class DatabaseBootstrapper(string connectionString, ILogger<DatabaseBootstrapper> logger)
{
    public async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var reconcile = new NpgsqlCommand("UPDATE background_jobs j SET status='Completed',progress_current=progress_total,completed_at=s.completed_at,error_message=NULL FROM ranking_snapshots s WHERE j.id=s.id AND j.job_type='ranking_snapshot' AND s.status='Completed' AND j.status<>'Completed'", connection, transaction))
            await reconcile.ExecuteNonQueryAsync(cancellationToken);
        await using (var resetImports = new NpgsqlCommand("UPDATE imports SET status='Queued',rows_processed=0,rows_total=NULL,report=NULL,completed_at=NULL WHERE id IN (SELECT (payload->>'importId')::uuid FROM background_jobs WHERE status='Running' AND job_type='csv_import')", connection, transaction))
            await resetImports.ExecuteNonQueryAsync(cancellationToken);
        await using (var resetSnapshots = new NpgsqlCommand("UPDATE ranking_snapshots SET status='Queued',completed_at=NULL,error_message=NULL WHERE id IN (SELECT id FROM background_jobs WHERE status='Running' AND job_type='ranking_snapshot')", connection, transaction))
            await resetSnapshots.ExecuteNonQueryAsync(cancellationToken);
        await using (var resetPdfs = new NpgsqlCommand("UPDATE pdf_extractions SET status='Queued',version=version+1,updated_at=now() WHERE id IN (SELECT id FROM background_jobs WHERE status='Running' AND job_type='pdf_extraction') AND status='Running'", connection, transaction))
            await resetPdfs.ExecuteNonQueryAsync(cancellationToken);
        await using (var resetJobs = new NpgsqlCommand("UPDATE background_jobs SET status='Queued',started_at=NULL,completed_at=NULL,error_message=NULL,progress_current=0 WHERE status='Running'", connection, transaction))
        {
            var recovered = await resetJobs.ExecuteNonQueryAsync(cancellationToken);
            await using (var progress = new NpgsqlCommand("UPDATE job_progress p SET phase=j.status,current_count=CASE WHEN j.status='Completed' THEN COALESCE(p.total_count,p.current_count) ELSE 0 END,updated_at=now() FROM background_jobs j WHERE j.id=p.job_id AND j.status IN ('Queued','Completed')",connection,transaction))
                await progress.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (recovered > 0) logger.LogWarning("Requeued {JobCount} interrupted background jobs", recovered);
        }
    }

    public async Task InitializeAsync(string? seedFile, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(77492341)", connection))
            await command.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            await using (var command = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
                await command.ExecuteNonQueryAsync(cancellationToken);
            var migrationDirectory = Path.Combine(AppContext.BaseDirectory, "Migrations");
            foreach (var migrationPath in Directory.GetFiles(migrationDirectory, "*.sql").Order(StringComparer.Ordinal))
            {
                var version = Path.GetFileNameWithoutExtension(migrationPath);
                await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version=@version)", connection);
                check.Parameters.AddWithValue("version", version);
                if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false)) continue;
                var sql = await File.ReadAllTextAsync(migrationPath, cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                    await migration.ExecuteNonQueryAsync(cancellationToken);
                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations(version) VALUES (@version)", connection, transaction))
                {
                    record.Parameters.AddWithValue("version", version);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Applied database migration {Migration}", version);
            }
            if (!string.IsNullOrWhiteSpace(seedFile) && File.Exists(seedFile))
                await SeedMembershipsAsync(connection, seedFile, cancellationToken);
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(77492341)", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task SeedMembershipsAsync(NpgsqlConnection connection, string path, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var reader = new StreamReader(path);
        _ = await reader.ReadLineAsync(cancellationToken);
        var count = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            if (fields.Count != 4 || fields[0].Length != 3 || !DateOnly.TryParse(fields[2], out var from))
                throw new InvalidDataException($"Invalid member_states.csv row: {count + 2}");
            DateOnly? to = string.IsNullOrWhiteSpace(fields[3]) ? null : DateOnly.Parse(fields[3]);
            await using (var state = new NpgsqlCommand("INSERT INTO member_states(country_code,country_name) VALUES (@code,@name) ON CONFLICT (country_code) DO UPDATE SET country_name=EXCLUDED.country_name", connection, transaction))
            {
                state.Parameters.AddWithValue("code", fields[0].ToUpperInvariant());
                state.Parameters.AddWithValue("name", fields[1]);
                await state.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var period = new NpgsqlCommand("INSERT INTO membership_periods(country_code,member_from,member_to) VALUES (@code,@from,@to) ON CONFLICT (country_code,member_from) DO UPDATE SET member_to=EXCLUDED.member_to", connection, transaction))
            {
                period.Parameters.AddWithValue("code", fields[0].ToUpperInvariant());
                period.Parameters.AddWithValue("from", from);
                period.Parameters.AddWithValue("to", (object?)to ?? DBNull.Value);
                await period.ExecuteNonQueryAsync(cancellationToken);
            }
            count++;
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Seeded {MembershipPeriods} membership periods from {SeedFile}", count, Path.GetFileName(path));
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
            else if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { fields.Add(value.ToString().Trim()); value.Clear(); }
            else value.Append(ch);
        }
        fields.Add(value.ToString().Trim());
        return fields;
    }
}

