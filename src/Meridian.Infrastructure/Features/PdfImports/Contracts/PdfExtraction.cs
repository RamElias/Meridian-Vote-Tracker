namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfExtraction(Guid Id, string FileName, string Status, int Version, string UploadedBy, DateTimeOffset CreatedAt, Guid? OriginalExtractionId, PdfDraft? Draft, IReadOnlyList<PdfIssue> Issues, string? Error, Guid? ImportId, string? ReviewedBy, DateTimeOffset? ReviewedAt, string? DecisionNote, PdfProgress? Progress);
