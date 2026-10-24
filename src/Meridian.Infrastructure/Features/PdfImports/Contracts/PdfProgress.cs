namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfProgress(string Phase, long Current, long? Total);
