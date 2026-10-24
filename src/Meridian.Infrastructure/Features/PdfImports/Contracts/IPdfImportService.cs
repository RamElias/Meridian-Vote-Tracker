namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public interface IPdfImportService
{
    Task<PdfExtraction> CreateAsync(string fileName, Stream source, string actor, string correlationId, CancellationToken ct);
    Task<IReadOnlyList<PdfExtraction>> ListAsync(int offset, int limit, CancellationToken ct);
    Task<PdfExtraction?> GetAsync(Guid id, CancellationToken ct);
    Task<Stream?> OpenSourceAsync(Guid id, CancellationToken ct);
    Task<PdfWriteResult> ReviewAsync(Guid id, PdfReviewRequest request, bool approve, string actor, string correlationId, CancellationToken ct);
    Task<PdfWriteResult> RejectAsync(Guid id, PdfDecisionRequest request, string actor, string correlationId, CancellationToken ct);
    Task<PdfWriteResult> ReparseAsync(Guid id, PdfDecisionRequest request, string actor, string correlationId, CancellationToken ct);
    Task<PdfWriteResult> RetryAsync(Guid id, PdfDecisionRequest request, string actor, string correlationId, CancellationToken ct);
}
