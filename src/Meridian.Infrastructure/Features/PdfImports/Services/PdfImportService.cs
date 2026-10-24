using System.Text;
using System.Text.Json;
using Meridian.Infrastructure.Features.Imports.Services;
using Meridian.Infrastructure.Features.PdfImports.Contracts;
using Meridian.Infrastructure.Features.PdfImports.Processing;
using Meridian.Infrastructure.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Features.PdfImports.Services;

public sealed class PdfImportService(NpgsqlDataSource source, IImportFileStorage storage, IDocumentAnalysisClient analysis, PdfDraftValidator validator) : IPdfImportService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Projection = "SELECT e.id,e.file_name,e.status,e.version,u.display_name,e.created_at,e.original_extraction_id,e.draft,e.issues,e.error_message,e.import_id,r.display_name,e.reviewed_at,e.decision_note,p.phase,p.current_count,p.total_count FROM pdf_extractions e JOIN app_users u ON u.user_id=e.uploaded_by_user_id LEFT JOIN app_users r ON r.user_id=e.reviewed_by_user_id LEFT JOIN job_progress p ON p.job_id=e.id";
    public async Task<PdfExtraction> CreateAsync(string fileName, Stream input, string actor, string correlationId, CancellationToken ct)
    {
        if (!analysis.IsConfigured)
            throw new InvalidDataException("PDF extraction is not configured. Set the Azure Document Intelligence endpoint and credentials.");
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a PDF file up to 20 MB.");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > 20 * 1024 * 1024)
                throw new InvalidDataException("PDF files must be up to 20 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        if (buffer.Length < 5 || Encoding.ASCII.GetString(buffer.GetBuffer(), 0, 5) != "%PDF-")
            throw new InvalidDataException("The uploaded file does not have a PDF signature.");
        buffer.Position = 0;
        var id = Guid.NewGuid();
        var stored = await storage.SaveAsync(id, buffer, ct, ".pdf");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@hash, 1))", connection, tx))
        {
            gate.Parameters.AddWithValue("hash", stored.Sha256);
            await gate.ExecuteNonQueryAsync(ct);
        }

        Guid? original = null;
        await using (var find = new NpgsqlCommand("SELECT id FROM pdf_extractions WHERE content_sha256=@hash AND status NOT IN ('Failed','Duplicate') ORDER BY created_at LIMIT 1", connection, tx))
        {
            find.Parameters.AddWithValue("hash", stored.Sha256);
            if (await find.ExecuteScalarAsync(ct)is Guid existing)
                original = existing;
        }

        await using (var job = new NpgsqlCommand("INSERT INTO background_jobs(id,job_type,status,payload,completed_at) VALUES (@id,'pdf_extraction',@status,jsonb_build_object('correlationId',@correlation),CASE WHEN @status='Completed' THEN now() END)", connection, tx))
        {
            job.Parameters.AddWithValue("id", id);
            job.Parameters.AddWithValue("status", original.HasValue ? "Completed" : "Queued");
            job.Parameters.AddWithValue("correlation", correlationId);
            await job.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = new NpgsqlCommand("INSERT INTO pdf_extractions(id,file_name,file_path,content_sha256,status,original_extraction_id,uploaded_by_user_id) VALUES (@id,@name,@path,@hash,@status,@original,@actor)", connection, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("name", Path.GetFileName(fileName));
            insert.Parameters.AddWithValue("path", stored.Key);
            insert.Parameters.AddWithValue("hash", stored.Sha256);
            insert.Parameters.AddWithValue("status", original.HasValue ? "Duplicate" : "Queued");
            insert.Parameters.Add("original", NpgsqlDbType.Uuid).Value = (object? )original ?? DBNull.Value;
            insert.Parameters.AddWithValue("actor", actor);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (await ReadCurrentAsync(id, connection, null, ct))!;
    }

    public async Task<IReadOnlyList<PdfExtraction>> ListAsync(int offset, int limit, CancellationToken ct)
    {
        await using var command = source.CreateCommand(Projection.Replace("e.draft,", "NULL::jsonb,") + " ORDER BY e.created_at DESC,e.id LIMIT @limit OFFSET @offset");
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        command.Parameters.AddWithValue("offset", Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<PdfExtraction>();
        while (await reader.ReadAsync(ct))
            items.Add(Read(reader));
        return items;
    }

    public async Task<PdfExtraction?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await ReadCurrentAsync(id, connection, null, ct);
    }

    private static async Task<PdfExtraction?> ReadCurrentAsync(Guid id, NpgsqlConnection connection, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(Projection + " WHERE e.id=@id", connection, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<Stream?> OpenSourceAsync(Guid id, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT file_path FROM pdf_extractions WHERE id=@id");
        command.Parameters.AddWithValue("id", id);
        var path = await command.ExecuteScalarAsync(ct) as string;
        return path is null ? null : await storage.OpenReadAsync(path, ct);
    }

    public async Task<PdfWriteResult> ReviewAsync(Guid id, PdfReviewRequest request, bool approve, string actor, string correlationId, CancellationToken ct)
    {
        if (request.Draft is null || request.Draft.Rows is null || request.Draft.Rows.Count > 2000 || request.Draft.Rows.Any(r => r is null))
            return new(false, Error: "Supply a draft with up to 2,000 rows.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var state = await LockAsync(id, connection, tx, ct);
        if (state is null)
            return new(false, NotFound: true);
        if (state.Value.Version != request.ExpectedVersion || state.Value.Status != "AwaitingReview")
            return new(false, Stale: true, Error: "This extraction changed. Reload its current state.", Current: await ReadCurrentAsync(id, connection, tx, ct));
        var draft = PdfDraftValidator.Normalize(request.Draft, state.Value.Original);
        var issues = await validator.ValidateAsync(draft, connection, tx, ct);
        if (approve && (issues.Any(i => i.Severity == "Error") || (!request.AcknowledgeWarnings && issues.Any(i => i.Severity == "Warning"))))
            return new(false, Error: "Correct blocking issues and acknowledge the source review warnings before approval.", Issues: issues);
        Guid? importId = null;
        if (approve)
        {
            static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at\n");
            foreach (var row in draft.Rows)
                csv.AppendLine(string.Join(',', new[] { draft.VoteId, draft.Session, draft.VoteDate, draft.Title, row.CountryCode, row.CountryName, row.Ballot, draft.PublishedAt }.Select(Cell)));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv.ToString()));
            var import = await new ImportService(source, storage).CreateWithinTransactionAsync(state.Value.FileName + ".reviewed.csv", correlationId, actor, stream, connection, tx, ct);
            importId = import.ImportId;
            await using var link = new NpgsqlCommand("UPDATE imports SET source_pdf_extraction_id=@pdf WHERE id=@id", connection, tx);
            link.Parameters.AddWithValue("pdf", id);
            link.Parameters.AddWithValue("id", importId);
            await link.ExecuteNonQueryAsync(ct);
        }

        await using (var update = new NpgsqlCommand("UPDATE pdf_extractions SET draft=@draft,issues=@issues,status=@status,version=version+1,updated_at=now(),reviewed_by_user_id=CASE WHEN @approved THEN @actor ELSE reviewed_by_user_id END,reviewed_at=CASE WHEN @approved THEN now() ELSE reviewed_at END,decision_note=@note,import_id=@import WHERE id=@id", connection, tx))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("draft", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(draft, Json));
            update.Parameters.AddWithValue("issues", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(issues, Json));
            update.Parameters.AddWithValue("status", approve ? "Accepted" : "AwaitingReview");
            update.Parameters.AddWithValue("approved", approve);
            update.Parameters.AddWithValue("actor", actor);
            update.Parameters.Add("note", NpgsqlDbType.Text).Value = (object? )request.Note ?? DBNull.Value;
            update.Parameters.Add("import", NpgsqlDbType.Uuid).Value = (object? )importId ?? DBNull.Value;
            await update.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(id, approve ? "PdfApproved" : "PdfDraftEdited", actor, correlationId, request.Note, state.Value.Draft, draft, importId, connection, tx, ct);
        await tx.CommitAsync(ct);
        return new(true, Current: await ReadCurrentAsync(id, connection, null, ct));
    }

    public async Task<PdfWriteResult> RejectAsync(Guid id, PdfDecisionRequest request, string actor, string correlationId, CancellationToken ct) => await ChangeStateAsync(id, request, "AwaitingReview", "Rejected", actor, correlationId, ct);
    public async Task<PdfWriteResult> RetryAsync(Guid id, PdfDecisionRequest request, string actor, string correlationId, CancellationToken ct)
    {
        if (!analysis.IsConfigured)
            return new(false, Error: "Configure Azure Document Intelligence before retrying.");
        return await ChangeStateAsync(id, request, "Failed", "Queued", actor, correlationId, ct);
    }

    private async Task<PdfWriteResult> ChangeStateAsync(Guid id, PdfDecisionRequest request, string required, string next, string actor, string correlationId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var state = await LockAsync(id, connection, tx, ct);
        if (state is null)
            return new(false, NotFound: true);
        if (state.Value.Status != required || state.Value.Version != request.ExpectedVersion)
            return new(false, Stale: true, Error: "This extraction changed. Reload its current state.", Current: await ReadCurrentAsync(id, connection, tx, ct));
        await using (var update = new NpgsqlCommand("UPDATE pdf_extractions SET status=@status,version=version+1,updated_at=now(),error_message=NULL,operation_url=CASE WHEN @status='Queued' THEN NULL ELSE operation_url END,reviewed_by_user_id=CASE WHEN @status='Rejected' THEN @actor ELSE reviewed_by_user_id END,reviewed_at=CASE WHEN @status='Rejected' THEN now() ELSE reviewed_at END,decision_note=@note WHERE id=@id; UPDATE background_jobs SET status=CASE WHEN @status='Queued' THEN 'Queued' ELSE 'Completed' END,started_at=NULL,completed_at=NULL,error_message=NULL,payload=jsonb_set(payload,'{correlationId}',to_jsonb(@correlation::text)) WHERE id=@id; DELETE FROM job_progress WHERE job_id=@id", connection, tx))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("status", next);
            update.Parameters.AddWithValue("actor", actor);
            update.Parameters.AddWithValue("correlation", correlationId);
            update.Parameters.Add("note", NpgsqlDbType.Text).Value = (object? )request.Note ?? DBNull.Value;
            await update.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(id, next == "Queued" ? "PdfRetryRequested" : "PdfRejected", actor, correlationId, request.Note, state.Value.Draft, null, null, connection, tx, ct);
        await tx.CommitAsync(ct);
        return new(true, Current: await ReadCurrentAsync(id, connection, null, ct));
    }

    private static async Task<(string Status, int Version, string FileName, PdfDraft? Original, PdfDraft? Draft)?> LockAsync(Guid id, NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT status,version,file_name,extracted_draft,draft FROM pdf_extractions WHERE id=@id FOR UPDATE", connection, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        PdfDraft? Draft(int i) => reader.IsDBNull(i) ? null : JsonSerializer.Deserialize<PdfDraft>(reader.GetString(i), Json);
        return (reader.GetString(0), reader.GetInt32(1), reader.GetString(2), Draft(3), Draft(4));
    }

    private static PdfExtraction Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : JsonSerializer.Deserialize<PdfDraft>(r.GetString(7), Json), JsonSerializer.Deserialize<PdfIssue[]>(r.GetString(8), Json) ?? [], r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : new(r.GetString(14), r.GetInt64(15), r.IsDBNull(16) ? null : r.GetInt64(16)));
    private static async Task AuditAsync(Guid id, string action, string actor, string correlation, string? note, object? before, object? after, Guid? import, NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("INSERT INTO audit_events(id,entity_type,entity_key,action,actor_user_id,old_value,new_value,source_import_id,note,correlation_id) VALUES (@event,'PdfExtraction',@key,@action,@actor,@before,@after,@import,@note,@correlation)", connection, tx);
        command.Parameters.AddWithValue("event", Guid.NewGuid());
        command.Parameters.AddWithValue("key", id.ToString());
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(before, Json));
        command.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(after, Json));
        command.Parameters.Add("import", NpgsqlDbType.Uuid).Value = (object? )import ?? DBNull.Value;
        command.Parameters.Add("note", NpgsqlDbType.Text).Value = (object? )note ?? DBNull.Value;
        command.Parameters.AddWithValue("correlation", correlation);
        await command.ExecuteNonQueryAsync(ct);
    }
}
