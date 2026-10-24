namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record DocumentAnalysisPoll(string Status, string? ResultJson = null);
