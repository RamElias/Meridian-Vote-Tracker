namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfDraft(string VoteId, string Session, string VoteDate, string Title, string PublishedAt, PdfTotals? PrintedTotals, IReadOnlyList<PdfDraftRow> Rows);
