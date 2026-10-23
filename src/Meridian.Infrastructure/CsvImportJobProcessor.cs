using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure;

public sealed class CsvImportJobProcessor(NpgsqlDataSource dataSource, CsvImportValidator validator, IImportFileStorage fileStorage, JobProgressReporter progress, ILogger<CsvImportJobProcessor> logger)
{
    private readonly record struct ClaimedJob(Guid JobId, Guid ImportId, string CorrelationId);
    private static readonly string[] RequiredHeaders = ["assembly_vote_id", "session", "vote_date", "resolution_title", "country_code", "country_name", "ballot", "published_at"];

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var job = await ClaimAsync(cancellationToken);
        if (job is null) return false;
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = job.Value.CorrelationId });
        try
        {
            await ResetStagingForRetryAsync(job.Value.ImportId, job.Value.JobId, cancellationToken);
            var import = await GetImportFileAsync(job.Value.ImportId, cancellationToken);
            await progress.ReportAsync(job.Value.JobId,"Reading",0,null,cancellationToken,true);
            var totalRows = await CountRowsAsync(job.Value.JobId, import.Path, cancellationToken);
            await progress.ReportAsync(job.Value.JobId,"Staging",0,totalRows,cancellationToken,true);
            await SetTotalAsync(job.Value.JobId, job.Value.ImportId, totalRows, cancellationToken);
            var stagedRows = await StageAsync(job.Value.JobId, job.Value.ImportId, import.Path, cancellationToken);
            await validator.ValidateAndPromoteAsync(job.Value.ImportId, job.Value.JobId, cancellationToken);
            logger.LogInformation("Import {ImportId} staged {RowCount} CSV rows", job.Value.ImportId, stagedRows);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "CSV import job {JobId} failed", job.Value.JobId);
            await using (var completed = dataSource.CreateCommand("SELECT status='Completed' FROM imports WHERE id=@id"))
            {
                completed.Parameters.AddWithValue("id",job.Value.ImportId);
                if (await completed.ExecuteScalarAsync(cancellationToken) is true) return true;
            }
            var message = exception is InvalidDataException ? exception.Message : $"Import processing failed. Reference: {job.Value.CorrelationId}";
            await MarkFailedAsync(job.Value.JobId, job.Value.ImportId, message, cancellationToken);
            await progress.ReportAsync(job.Value.JobId,"Failed",0,null,cancellationToken,true);
        }
        return true;
    }

    private async Task ResetStagingForRetryAsync(Guid importId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clearRows = new NpgsqlCommand("DELETE FROM import_staging_rows WHERE import_id=@id", connection, transaction))
        {
            clearRows.Parameters.AddWithValue("id", importId);
            await clearRows.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var resetImport = new NpgsqlCommand("UPDATE imports SET rows_processed=0,rows_total=NULL,report=NULL,completed_at=NULL WHERE id=@id", connection, transaction))
        {
            resetImport.Parameters.AddWithValue("id", importId);
            await resetImport.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var resetJob = new NpgsqlCommand("UPDATE background_jobs SET progress_current=0,progress_total=NULL,completed_at=NULL,error_message=NULL WHERE id=@id", connection, transaction))
        {
            resetJob.Parameters.AddWithValue("id", jobId);
            await resetJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid jobId;
        Guid importId;
        string correlationId;
        await using (var claim = new NpgsqlCommand("SELECT id,(payload->>'importId')::uuid,COALESCE(payload->>'correlationId','') FROM background_jobs WHERE job_type='csv_import' AND status='Queued' ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1", connection, transaction))
        await using (var reader = await claim.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            jobId = reader.GetGuid(0);
            importId = reader.GetGuid(1);
            correlationId = reader.GetString(2);
        }
        await using (var updateJob = new NpgsqlCommand("UPDATE background_jobs SET status='Running',started_at=now() WHERE id=@id", connection, transaction))
        {
            updateJob.Parameters.AddWithValue("id", jobId);
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var updateImport = new NpgsqlCommand("UPDATE imports SET status='Running' WHERE id=@id", connection, transaction))
        {
            updateImport.Parameters.AddWithValue("id", importId);
            await updateImport.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ClaimedJob(jobId, importId, correlationId);
    }

    private async Task<(string Path, string FileName)> GetImportFileAsync(Guid importId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT file_path,file_name FROM imports WHERE id=@id");
        command.Parameters.AddWithValue("id", importId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"Import {importId} does not exist.");
        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task<long> StageAsync(Guid jobId, Guid importId, string path, CancellationToken cancellationToken)
    {
        await using var file = await fileStorage.OpenReadAsync(path, cancellationToken);
        using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
        await using var rows = CsvRecordReader.ReadAsync(reader, cancellationToken).GetAsyncEnumerator(cancellationToken);
        if (!await rows.MoveNextAsync()) throw new InvalidDataException("The CSV file is empty.");
        var headers = rows.Current.Select(header => header.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToArray();
        if (headers.Length != RequiredHeaders.Length || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length || RequiredHeaders.Except(headers, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The CSV header must contain each required assignment column exactly once.");
        var batch = new List<string[]>(2000);
        long rowNumber = 1;
        long staged = 0;
        while (await rows.MoveNextAsync())
        {
            rowNumber++;
            var values = rows.Current.ToArray();
            batch.Add(values);
            if (batch.Count >= 2000)
            {
                await SaveBatchAsync(jobId, importId, headers, rowNumber - batch.Count + 1, staged + batch.Count, batch, cancellationToken);
                staged += batch.Count;
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await SaveBatchAsync(jobId, importId, headers, rowNumber - batch.Count + 1, staged + batch.Count, batch, cancellationToken);
            staged += batch.Count;
        }
        return staged;
    }

    private async Task<long> CountRowsAsync(Guid jobId, string path, CancellationToken cancellationToken)
    {
        await using var file = await fileStorage.OpenReadAsync(path, cancellationToken);
        using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
        long rows = 0;
        await foreach (var _ in CsvRecordReader.ReadAsync(reader, cancellationToken))
        {
            rows++;
            if (rows % 5000 == 0) await progress.ReportAsync(jobId,"Reading",Math.Max(0,rows-1),null,cancellationToken);
        }
        return Math.Max(0, rows - 1);
    }

    private async Task SetTotalAsync(Guid jobId, Guid importId, long totalRows, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE imports SET rows_total=@total WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("total", totalRows);
            update.Parameters.AddWithValue("id", importId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = new NpgsqlCommand("UPDATE background_jobs SET progress_total=@total WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("total", totalRows);
            update.Parameters.AddWithValue("id", jobId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
    private async Task SaveBatchAsync(Guid jobId, Guid importId, string[] headers, long firstRow, long progressCurrent, List<string[]> batch, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var importer = await connection.BeginBinaryImportAsync("COPY import_staging_rows(import_id,row_number,raw_values,normalized_values,normalizations) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            for (var offset = 0; offset < batch.Count; offset++)
            {
                var values = batch[offset];
                var raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var column = 0; column < headers.Length; column++)
                    raw[headers[column]] = column < values.Length ? values[column] : null;
                raw["__field_count_valid"] = values.Length == headers.Length;
                if (values.Length > headers.Length) raw["__extra_columns"] = values.Skip(headers.Length).ToArray();
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(importId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(firstRow + offset, NpgsqlDbType.Bigint, cancellationToken);
                await importer.WriteAsync(JsonSerializer.Serialize(raw), NpgsqlDbType.Jsonb, cancellationToken);
                var normalized = CsvValueNormalizer.Normalize(raw);
                await importer.WriteAsync(JsonSerializer.Serialize(normalized.Values), NpgsqlDbType.Jsonb, cancellationToken);
                await importer.WriteAsync(JsonSerializer.Serialize(normalized.Changes), NpgsqlDbType.Jsonb, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }
        var stagedProgress = progressCurrent;
        await using (var update = new NpgsqlCommand("UPDATE imports SET rows_processed=@progress WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("progress", stagedProgress);
            update.Parameters.AddWithValue("id", importId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = new NpgsqlCommand("UPDATE background_jobs SET progress_current=@progress WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("progress", stagedProgress);
            update.Parameters.AddWithValue("id", jobId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await progress.ReportAsync(jobId,"Staging",progressCurrent,null,cancellationToken);
    }

    private async Task MarkFailedAsync(Guid jobId, Guid importId, string message, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var updateImport = new NpgsqlCommand("UPDATE imports SET status='Failed',report=jsonb_build_object('error',@message),completed_at=now() WHERE id=@id AND status<>'Completed'", connection, transaction))
        {
            updateImport.Parameters.AddWithValue("message", message);
            updateImport.Parameters.AddWithValue("id", importId);
            await updateImport.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var updateJob = new NpgsqlCommand("UPDATE background_jobs SET status='Failed',error_message=@message,completed_at=now() WHERE id=@id AND status<>'Completed'", connection, transaction))
        {
            updateJob.Parameters.AddWithValue("message", message);
            updateJob.Parameters.AddWithValue("id", jobId);
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}









