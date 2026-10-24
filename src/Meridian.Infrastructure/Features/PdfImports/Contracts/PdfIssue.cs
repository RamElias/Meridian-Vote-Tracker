namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfIssue(string Code, string Severity, string Message, int? RowNumber = null);
