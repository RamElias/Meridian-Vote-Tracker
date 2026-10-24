namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfDraftRow(int RowNumber, int PageNumber, string CountryCode, string CountryName, string Ballot, double? Confidence, string SourceText);
