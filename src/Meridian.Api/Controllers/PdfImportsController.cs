using System.Security.Claims;
using Meridian.Infrastructure.Features.PdfImports.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Controllers;
[ApiController]
[Authorize]
public sealed class PdfImportsController(IPdfImportService service) : ApiControllerBase
{
    [HttpPost("/api/v1/pdf-imports", Name = "CreatePdfImport")]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        if (!Request.HasFormContentType)
            return ApiProblem(title: "Invalid upload", detail: "Upload a PDF in the multipart 'file' field.", statusCode: 400);
        var form = await Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length is < 5 or > 20 * 1024 * 1024)
            return ApiProblem(title: "Invalid upload", detail: "Choose a PDF file up to 20 MB.", statusCode: 400);
        try
        {
            await using var input = file.OpenReadStream();
            var item = await service.CreateAsync(file.FileName, input, Actor, HttpContext.TraceIdentifier, ct);
            return item.Status == "Duplicate" ? Ok(item) : Accepted($"/api/v1/pdf-imports/{item.Id}", item);
        }
        catch (InvalidDataException ex)
        {
            return ApiProblem(title: "Invalid PDF upload", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpGet("/api/v1/pdf-imports", Name = "ListPdfImports")]
    public async Task<IActionResult> List([FromQuery] int? offset, [FromQuery] int? limit, CancellationToken ct) => Ok(await service.ListAsync(offset ?? 0, limit ?? 50, ct));
    [HttpGet("/api/v1/pdf-imports/{id:guid}", Name = "GetPdfImport")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var item = await service.GetAsync(id, ct);
        return item is null ? ApiProblem(title: "PDF extraction not found", statusCode: 404) : Ok(item);
    }

    [HttpGet("/api/v1/pdf-imports/{id:guid}/source", Name = "GetPdfSource")]
    public async Task<IActionResult> Source(Guid id, CancellationToken ct)
    {
        var input = await service.OpenSourceAsync(id, ct);
        return input is null ? ApiProblem(title: "PDF source not found", statusCode: 404) : File(input, "application/pdf");
    }

    [HttpPut("/api/v1/pdf-imports/{id:guid}/draft", Name = "SavePdfDraft")]
    public async Task<IActionResult> Save(Guid id, [FromBody] PdfReviewRequest request, CancellationToken ct) => Map(await service.ReviewAsync(id, request, false, Actor, HttpContext.TraceIdentifier, ct));
    [HttpPost("/api/v1/pdf-imports/{id:guid}/approve", Name = "ApprovePdfDraft")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] PdfReviewRequest request, CancellationToken ct) => Map(await service.ReviewAsync(id, request, true, Actor, HttpContext.TraceIdentifier, ct));
    [HttpPost("/api/v1/pdf-imports/{id:guid}/reject", Name = "RejectPdfDraft")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] PdfDecisionRequest request, CancellationToken ct) => Map(await service.RejectAsync(id, request, Actor, HttpContext.TraceIdentifier, ct));
    [HttpPost("/api/v1/pdf-imports/{id:guid}/retry", Name = "RetryPdfImport")]
    public async Task<IActionResult> Retry(Guid id, [FromBody] PdfDecisionRequest request, CancellationToken ct) => Map(await service.RetryAsync(id, request, Actor, HttpContext.TraceIdentifier, ct));
    [HttpPost("/api/v1/pdf-imports/{id:guid}/reparse",Name="ReparsePdfImport")]
    public async Task<IActionResult> Reparse(Guid id,[FromBody]PdfDecisionRequest request,CancellationToken ct) => Map(await service.ReparseAsync(id,request,Actor,HttpContext.TraceIdentifier,ct));
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private IActionResult Map(PdfWriteResult result) => result.Applied ? Ok(result.Current) : ApiProblem(title: result.NotFound ? "PDF extraction not found" : result.Stale ? "PDF extraction changed" : "PDF review requires attention", detail: result.Error, statusCode: result.NotFound ? 404 : result.Stale ? 409 : 422, extensions: new Dictionary<string, object?> { { "issues", result.Issues }, { "current", result.Current } });
}
