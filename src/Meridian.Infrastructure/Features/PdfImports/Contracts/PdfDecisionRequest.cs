namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public sealed record PdfDecisionRequest(int ExpectedVersion, string? Note);
