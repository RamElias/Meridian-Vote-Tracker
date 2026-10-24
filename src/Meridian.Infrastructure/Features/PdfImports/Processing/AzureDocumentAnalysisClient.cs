using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Meridian.Infrastructure.Features.PdfImports.Contracts;

namespace Meridian.Infrastructure.Features.PdfImports.Processing;

public sealed class AzureDocumentAnalysisClient : IDocumentAnalysisClient, IDisposable
{
    private readonly HttpClient client;
    private readonly IConfiguration configuration;
    private readonly TokenCredential credential;
    public AzureDocumentAnalysisClient(HttpClient client, IConfiguration configuration, TokenCredential? credential = null)
    {
        this.client = client;
        this.configuration = configuration;
        this.credential = credential ?? new DefaultAzureCredential();
    }

    public bool IsConfigured => Uri.TryCreate(configuration["DocumentIntelligence:Endpoint"], UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https";
    private Uri Endpoint => new(configuration["DocumentIntelligence:Endpoint"]!.TrimEnd('/') + "/");

    public async Task<string> StartAsync(Stream pdf, string correlationId, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidDataException("Configure DocumentIntelligence:Endpoint before extracting PDFs.");
        var uri = new Uri(Endpoint, "documentintelligence/documentModels/prebuilt-layout:analyze?api-version=2024-11-30&stringIndexType=utf16CodeUnit");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = new StreamContent(pdf);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        await AuthorizeAsync(request, correlationId, ct);
        using var response = await client.SendAsync(request, ct);
        EnsureSuccess(response);
        if (!response.Headers.TryGetValues("Operation-Location", out var locations))
            throw new InvalidDataException("Document Intelligence returned no operation reference. Retry the extraction.");
        var location = locations.Single();
        ValidateOperation(location);
        return location;
    }

    public async Task<DocumentAnalysisPoll> PollAsync(string operationUrl, string correlationId, CancellationToken ct)
    {
        ValidateOperation(operationUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, operationUrl);
        await AuthorizeAsync(request, correlationId, ct);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > 20 * 1024 * 1024)
            throw new InvalidDataException("Document analysis exceeds the supported result size.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 20 * 1024 * 1024)
                throw new InvalidDataException("Document analysis exceeds the supported result size.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString() ?? "";
        if (status == "failed")
            throw new InvalidDataException("Document Intelligence could not analyze this PDF. Check the scan and retry.");
        if (status == "succeeded")
            return new(status, root.GetProperty("analyzeResult").GetRawText());
        if (status is not ("running" or "notStarted"))
            throw new InvalidDataException("Document Intelligence returned an unsupported operation state.");
        return new(status);
    }

    private void ValidateOperation(string operationUrl)
    {
        if (!IsConfigured || !Uri.TryCreate(operationUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Authority != Endpoint.Authority || !uri.AbsolutePath.StartsWith("/documentintelligence/", StringComparison.Ordinal))
            throw new InvalidDataException("Document Intelligence returned an invalid operation reference.");
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, string correlationId, CancellationToken ct)
    {
        request.Headers.Add("x-ms-client-request-id", correlationId);
        var key = configuration["DocumentIntelligence:ApiKey"];
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        else
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var message = (int)response.StatusCode switch
        {
            401 or 403 => "Document Intelligence access was denied. Check its credentials and resource permissions.",
            429 => "Document Intelligence quota or rate limit was reached. Try again later.",
            400 or 413 or 415 => "Document Intelligence rejected this file. Check its format, size and scan quality.",
            _ => "Document Intelligence is temporarily unavailable. Retry the extraction."
        };
        throw new InvalidDataException(message);
    }

    public void Dispose() => client.Dispose();
}
