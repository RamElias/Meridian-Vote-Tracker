namespace Meridian.Infrastructure.Features.PdfImports.Contracts;

public interface IDocumentAnalysisClient
{
    bool IsConfigured { get; }

    Task<string> StartAsync(Stream pdf, string correlationId, CancellationToken ct);
    Task<DocumentAnalysisPoll> PollAsync(string operationUrl, string correlationId, CancellationToken ct);
}
