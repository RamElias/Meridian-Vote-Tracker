using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure;

public sealed record CreatedImport(Guid ImportId, string Status, Guid? OriginalImportId);

public sealed class ImportService(NpgsqlDataSource dataSource, string? configuredStoragePath)
{
    public async Task<CreatedImport> CreateAsync(string fileName, string correlationId, string uploadedByUserId, Stream source, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .csv files are accepted.");
        var importId = Guid.NewGuid();
        var storageRoot = configuredStoragePath;
        if (string.IsNullOrWhiteSpace(storageRoot)) storageRoot = Path.Combine(AppContext.BaseDirectory, "uploads");
        Directory.CreateDirectory(storageRoot);
        var storedPath = Path.Combine(storageRoot, $"{importId:N}.csv");
        await using (var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await source.CopyToAsync(output, cancellationToken);
        string hash;
        await using (var hashStream = File.OpenRead(storedPath))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
        var safeName = Path.GetFileName(fileName);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@hash, 0))", connection, transaction))
        {
            advisoryLock.Parameters.AddWithValue("hash", hash);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }
        Guid? originalId = null;
        await using (var find = new NpgsqlCommand("SELECT id FROM imports WHERE content_sha256=@hash AND status IN ('Queued','Running','Staged','Completed','Duplicate') ORDER BY created_at LIMIT 1", connection, transaction))
        {
            find.Parameters.AddWithValue("hash", hash);
            var result = await find.ExecuteScalarAsync(cancellationToken);
            if (result is Guid id) originalId = id;
        }
        var status = originalId.HasValue ? "Duplicate" : "Queued";
        await using (var insert = new NpgsqlCommand("INSERT INTO imports(id,file_name,content_sha256,file_path,status,original_import_id,report,completed_at,uploaded_by_user_id) VALUES (@id,@name,@hash,@path,@status,@original,@report,@completed,@uploadedBy)", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", importId);
            insert.Parameters.AddWithValue("name", safeName);
            insert.Parameters.AddWithValue("hash", hash);
            insert.Parameters.AddWithValue("path", storedPath);
            insert.Parameters.AddWithValue("status", status);
            insert.Parameters.AddWithValue("original", (object?)originalId ?? DBNull.Value);
            insert.Parameters.AddWithValue("report", originalId.HasValue ? NpgsqlDbType.Jsonb : NpgsqlDbType.Jsonb, originalId.HasValue ? JsonSerializer.Serialize(new { message = "Identical file content was already submitted; processing was skipped.", originalImportId = originalId }) : (object)DBNull.Value);
            insert.Parameters.AddWithValue("completed", originalId.HasValue ? DateTimeOffset.UtcNow : DBNull.Value);
            insert.Parameters.AddWithValue("uploadedBy", uploadedByUserId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!originalId.HasValue)
        {
            await using var job = new NpgsqlCommand("INSERT INTO background_jobs(id,job_type,status,payload) VALUES (@id,'csv_import','Queued',jsonb_build_object('importId',@importId,'correlationId',@correlationId))", connection, transaction);
            job.Parameters.AddWithValue("id", Guid.NewGuid());
            job.Parameters.AddWithValue("importId", importId.ToString());
            job.Parameters.AddWithValue("correlationId", correlationId);
            await job.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new CreatedImport(importId, status, originalId);
    }

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT i.id,i.file_name,i.status,i.rows_processed,i.rows_total,i.created_at,i.completed_at,i.original_import_id,u.display_name FROM imports i LEFT JOIN app_users u ON u.user_id=i.uploaded_by_user_id ORDER BY i.created_at DESC LIMIT 200";
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var imports = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            imports.Add(new { id = reader.GetGuid(0), fileName = reader.GetString(1), status = reader.GetString(2), rowsProcessed = reader.GetInt64(3), rowsTotal = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4), createdAt = reader.GetFieldValue<DateTimeOffset>(5), completedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6), originalImportId = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7), uploadedBy = reader.IsDBNull(8) ? null : reader.GetString(8) });
        return imports;
    }

    public async Task<IReadOnlyList<object>> ListRowsAsync(Guid id, long afterRow, int limit, string? classification, CancellationToken cancellationToken)
    {
        const string sql = "SELECT row_number,classification,reason,raw_values FROM import_staging_rows WHERE import_id=@id AND row_number>@after AND (@classification IS NULL OR classification=@classification) ORDER BY row_number LIMIT @limit";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("after", afterRow);
        command.Parameters.AddWithValue("classification", (object?)classification ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new { rowNumber = reader.GetInt64(0), classification = reader.IsDBNull(1) ? null : reader.GetString(1), reason = reader.IsDBNull(2) ? null : reader.GetString(2), values = reader.GetFieldValue<JsonDocument>(3).RootElement.Clone() });
        return rows;
    }
    public async Task<object?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT i.id,i.file_name,i.status,i.rows_processed,i.rows_total,i.created_at,i.completed_at,i.original_import_id,i.report,u.display_name FROM imports i LEFT JOIN app_users u ON u.user_id=i.uploaded_by_user_id WHERE i.id=@id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new { id = reader.GetGuid(0), fileName = reader.GetString(1), status = reader.GetString(2), rowsProcessed = reader.GetInt64(3), rowsTotal = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4), createdAt = reader.GetFieldValue<DateTimeOffset>(5), completedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6), originalImportId = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7), report = reader.IsDBNull(8) ? (object?)null : reader.GetFieldValue<JsonDocument>(8).RootElement.Clone(), uploadedBy = reader.IsDBNull(9) ? null : reader.GetString(9) };
    }
}
