namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfReviewRequest(int ExpectedVersion, PdfDraft Draft, string? Note, bool AcknowledgeWarnings = false);
