using Meridian.Infrastructure.Features.Imports.Contracts;
using Meridian.Infrastructure.Storage;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Features.Imports.Services;

public sealed class ImportService(NpgsqlDataSource dataSource, IImportFileStorage fileStorage) : IImportService
{
    public async Task<CreatedImport> CreateAsync(string fileName, string correlationId, string uploadedByUserId, Stream source, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .csv files are accepted.");
        var importId = Guid.NewGuid();
        var storedFile = await fileStorage.SaveAsync(importId, source, cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await EnqueuePreparedAsync(fileName, correlationId, uploadedByUserId, importId, storedFile, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal async Task<CreatedImport> CreateWithinTransactionAsync(string fileName, string correlationId, string uploadedByUserId,
        Stream source, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var importId = Guid.NewGuid();
        var storedFile = await fileStorage.SaveAsync(importId, source, cancellationToken);
        return await EnqueuePreparedAsync(fileName, correlationId, uploadedByUserId, importId, storedFile, connection, transaction, cancellationToken);
    }

    private static async Task<CreatedImport> EnqueuePreparedAsync(string fileName, string correlationId, string uploadedByUserId,
        Guid importId, StoredImportFile storedFile, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var storedPath = storedFile.Key;
        var hash = storedFile.Sha256;
        var safeName = Path.GetFileName(fileName);
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
        return new CreatedImport(importId, status, originalId);
    }

    public async Task<IReadOnlyList<ImportSummaryResponse>> ListAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT i.id,
            i.file_name,
            i.status,
            i.rows_processed,
            i.rows_total,
            i.created_at,
            i.completed_at,
            i.original_import_id,
            u.display_name,
            (
            SELECT jsonb_build_object('phase',
            p.phase,
            'current',
            p.current_count,
            'total',
            p.total_count,
            'unit',
            p.unit,
            'updatedAt',
            p.updated_at)
            FROM job_progress p
            JOIN background_jobs j ON j.id=p.job_id
            WHERE j.job_type='csv_import'
            AND j.payload->>'importId'=i.id::text LIMIT 1) AS progress
            FROM imports i
            LEFT JOIN app_users u ON u.user_id=i.uploaded_by_user_id
            ORDER BY i.created_at DESC,
            i.id LIMIT @limit OFFSET @offset
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        command.Parameters.AddWithValue("offset", Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var imports = new List<ImportSummaryResponse>();
        while (await reader.ReadAsync(cancellationToken))
            imports.Add(new ImportSummaryResponse { Id = reader.GetGuid(0), FileName = reader.GetString(1), Status = reader.GetString(2), RowsProcessed = reader.GetInt64(3), RowsTotal = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4), CreatedAt = reader.GetFieldValue<DateTimeOffset>(5), CompletedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6), OriginalImportId = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7), UploadedBy = reader.IsDBNull(8) ? null : reader.GetString(8), Progress = reader.IsDBNull(9) ? (System.Text.Json.JsonElement?)null : reader.GetFieldValue<JsonDocument>(9).RootElement.Clone() });
        return imports;
    }

    public async Task<IReadOnlyList<ImportRowResponse>> ListRowsAsync(Guid id, long afterRow, int limit, string? classification, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT row_number,
            classification,
            reason,
            raw_values,
            normalized_values,
            normalizations,
            ballot_classification,
            metadata_conflict_id
            FROM import_staging_rows
            WHERE import_id=@id
            AND row_number>@after
            AND (@classification IS NULL
            OR classification=@classification)
            ORDER BY row_number LIMIT @limit
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("after", afterRow);
        // The filter is optional. Set its PostgreSQL type explicitly so the
        // null case is sent as a typed text parameter on every import row query.
        command.Parameters.Add("classification", NpgsqlDbType.Text).Value = (object?)classification ?? DBNull.Value;
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ImportRowResponse>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new ImportRowResponse { RowNumber = reader.GetInt64(0), Classification = reader.IsDBNull(1) ? null : reader.GetString(1), Reason = reader.IsDBNull(2) ? null : reader.GetString(2), Values = reader.GetFieldValue<JsonDocument>(3).RootElement.Clone(), NormalizedValues = reader.GetFieldValue<JsonDocument>(4).RootElement.Clone(), Normalizations = reader.GetFieldValue<JsonDocument>(5).RootElement.Clone(), BallotClassification = reader.IsDBNull(6) ? null : reader.GetString(6), MetadataConflictId = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7) });
        return rows;
    }
    public async Task<ImportDetailResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT i.id,
            i.file_name,
            i.status,
            i.rows_processed,
            i.rows_total,
            i.created_at,
            i.completed_at,
            i.original_import_id,
            i.report,
            u.display_name,
            (
            SELECT jsonb_build_object('phase',
            p.phase,
            'current',
            p.current_count,
            'total',
            p.total_count,
            'unit',
            p.unit,
            'updatedAt',
            p.updated_at)
            FROM job_progress p
            JOIN background_jobs j ON j.id=p.job_id
            WHERE j.job_type='csv_import'
            AND j.payload->>'importId'=i.id::text LIMIT 1) AS progress
            FROM imports i
            LEFT JOIN app_users u ON u.user_id=i.uploaded_by_user_id
            WHERE i.id=@id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ImportDetailResponse { Id = reader.GetGuid(0), FileName = reader.GetString(1), Status = reader.GetString(2), RowsProcessed = reader.GetInt64(3), RowsTotal = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4), CreatedAt = reader.GetFieldValue<DateTimeOffset>(5), CompletedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6), OriginalImportId = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7), Report = reader.IsDBNull(8) ? (JsonElement?)null : reader.GetFieldValue<JsonDocument>(8).RootElement.Clone(), UploadedBy = reader.IsDBNull(9) ? null : reader.GetString(9), Progress = reader.IsDBNull(10) ? (System.Text.Json.JsonElement?)null : reader.GetFieldValue<JsonDocument>(10).RootElement.Clone() };
    }
}
