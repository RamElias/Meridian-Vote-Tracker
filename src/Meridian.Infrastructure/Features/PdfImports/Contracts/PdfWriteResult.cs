namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfWriteResult(bool Applied, bool NotFound = false, bool Stale = false, string? Error = null, IReadOnlyList<PdfIssue>? Issues = null, PdfExtraction? Current = null);
