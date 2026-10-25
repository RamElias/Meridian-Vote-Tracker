using System.Text.Json;
using Meridian.Infrastructure.BackgroundJobs;
using Meridian.Infrastructure.Features.PdfImports.Contracts;
using Meridian.Infrastructure.Features.PdfImports.Services;
using Meridian.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Features.PdfImports.Processing;

public sealed class PdfExtractionJobProcessor(NpgsqlDataSource source, IImportFileStorage storage, IDocumentAnalysisClient client, PdfLayoutParser parser, PdfDraftValidator validator, JobProgressReporter progress, IConfiguration configuration, ILogger<PdfExtractionJobProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken stoppingToken)
    {
        Guid id;
        string path;
        string correlation;
        string? operation;
        await using (var connection = await source.OpenConnectionAsync(stoppingToken))
        await using (var tx = await connection.BeginTransactionAsync(stoppingToken))
        {
            await using (var claim = new NpgsqlCommand("""
            SELECT j.id,
            e.file_path,
            COALESCE(j.payload->>'correlationId',
            j.id::text),
            e.operation_url
            FROM background_jobs j
            JOIN pdf_extractions e ON e.id=j.id
            WHERE j.job_type='pdf_extraction'
            AND j.status='Queued'
            AND e.status='Queued'
            ORDER BY j.created_at FOR
            UPDATE OF j,
            e SKIP LOCKED LIMIT 1
            """, connection, tx))
            await using (var reader = await claim.ExecuteReaderAsync(stoppingToken))
            {
                if (!await reader.ReadAsync(stoppingToken))
                    return false;
                id = reader.GetGuid(0);
                path = reader.GetString(1);
                correlation = reader.GetString(2);
                operation = reader.IsDBNull(3) ? null : reader.GetString(3);
            }

            await using (var claim = new NpgsqlCommand("UPDATE background_jobs SET status='Running',started_at=now(),completed_at=NULL,error_message=NULL WHERE id=@id; UPDATE pdf_extractions SET status='Running',version=version+1,updated_at=now(),error_message=NULL WHERE id=@id", connection, tx))
            {
                claim.Parameters.AddWithValue("id", id);
                await claim.ExecuteNonQueryAsync(stoppingToken);
            }

            await tx.CommitAsync(stoppingToken);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { { "CorrelationId", correlation }, { "PdfExtractionId", id } });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var seconds = int.TryParse(configuration["DocumentIntelligence:TimeoutSeconds"], out var configured) ? Math.Clamp(configured, 1, 600) : 300;
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var ct = deadline.Token;
        try
        {
            await progress.ReportAsync(id, "Extracting", 0, null, ct, true, "polls");
            if (operation is null)
            {
                await using var pdf = await storage.OpenReadAsync(path, ct);
                operation = await client.StartAsync(pdf, correlation, ct);
                await using var save = source.CreateCommand("UPDATE pdf_extractions SET operation_url=@operation WHERE id=@id");
                save.Parameters.AddWithValue("id", id);
                save.Parameters.AddWithValue("operation", operation);
                await save.ExecuteNonQueryAsync(ct);
            }

            string? result = null;
            long polls = 0;
            while (result is null)
            {
                var poll = await client.PollAsync(operation, correlation, ct);
                result = poll.ResultJson;
                await progress.ReportAsync(id, "Extracting", ++polls, null, ct, unit: "polls");
                if (result is null)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            await using (var save = source.CreateCommand("UPDATE pdf_extractions SET analysis=@analysis WHERE id=@id"))
            {
                save.Parameters.AddWithValue("id", id);
                save.Parameters.AddWithValue("analysis", NpgsqlDbType.Jsonb, result);
                await save.ExecuteNonQueryAsync(ct);
            }

            await progress.ReportAsync(id, "Validating", 0, null, ct, true);
            var rawDraft = parser.Parse(result);
            var draft = PdfDraftValidator.Normalize(rawDraft, rawDraft);
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var issues = await validator.ValidateAsync(draft, connection, tx, ct);
            await using (var complete = new NpgsqlCommand("""
            UPDATE pdf_extractions
            SET status='AwaitingReview',
            draft=@draft,
            extracted_draft=@draft,
            issues=@issues,
            version=version+1,
            updated_at=now(),
            error_message=NULL
            WHERE id=@id;
            UPDATE background_jobs
            SET status='Completed',
            completed_at=now(),
            error_message=NULL
            WHERE id=@id
            """, connection, tx))
            {
                complete.Parameters.AddWithValue("id", id);
                complete.Parameters.AddWithValue("draft", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(draft, PdfImportService.Json));
                complete.Parameters.AddWithValue("issues", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(issues, PdfImportService.Json));
                await complete.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            await progress.ReportAsync(id, "AwaitingReview", draft.Rows.Count, draft.Rows.Count, stoppingToken, true);
            logger.LogInformation("PDF extraction {ExtractionId} is ready for human review with {Rows} rows", id, draft.Rows.Count);
        }
        catch (OperationCanceledException)when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "PDF extraction {ExtractionId} failed", id);
            await using var failed = source.CreateCommand("""
            UPDATE pdf_extractions
            SET status='Failed',
            error_message=@error,
            version=version+1,
            updated_at=now()
            WHERE id=@id
            AND status='Running';
            UPDATE background_jobs
            SET status='Failed',
            error_message=@error,
            completed_at=now()
            WHERE id=@id
            AND status='Running'
            """);
            var message = exception is OperationCanceledException ? "PDF extraction timed out. Retry when the document service is available." : exception is InvalidDataException ? exception.Message : "PDF extraction failed. Check the scan or service configuration and retry.";
            failed.Parameters.AddWithValue("id", id);
            failed.Parameters.AddWithValue("error", message + " Reference: " + correlation);
            var changed = await failed.ExecuteNonQueryAsync(stoppingToken);
            if (changed > 0)
                await progress.ReportAsync(id, "Failed", 0, null, stoppingToken, true);
        }

        return true;
    }
}
