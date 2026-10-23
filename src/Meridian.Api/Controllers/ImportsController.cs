using System.Security.Claims;
using Meridian.Infrastructure.Features.Imports.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Controllers;

[ApiController]
public sealed class ImportsController(IImportService imports) : ApiControllerBase
{
    [Authorize]
    [HttpPost("/api/v1/imports", Name = "CreateImport")]
    public async Task<IActionResult> CreateImport(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return ApiProblem(title: "Invalid upload", detail: "Expected multipart/form-data with a CSV file in the 'file' field.", statusCode: StatusCodes.Status400BadRequest);
        var form = await Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return ApiProblem(title: "Invalid upload", detail: "The multipart form must contain a non-empty 'file' CSV.", statusCode: StatusCodes.Status400BadRequest);
        try
        {
            await using var stream = file.OpenReadStream();
            var result = await imports.CreateAsync(file.FileName, HttpContext.TraceIdentifier, User.FindFirstValue(ClaimTypes.NameIdentifier)!, stream, ct);
            var location = $"/api/v1/imports/{result.ImportId}";
            return result.Status == "Duplicate" ? Ok(result) : Accepted(location, result);
        }
        catch (InvalidDataException exception)
        {
            return ApiProblem(title: "Invalid upload", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [Authorize]
    [HttpGet("/api/v1/imports", Name = "ListImports")]
    public async Task<IActionResult> ListImports([FromQuery] int? offset, [FromQuery] int? limit, CancellationToken ct)
    { return Ok(await imports.ListAsync(offset ?? 0, limit ?? 100, ct)); }

    [Authorize]
    [HttpGet("/api/v1/imports/{id:guid}/rows", Name = "ListImportRows")]
    public async Task<IActionResult> ListImportRows([FromRoute] Guid id, [FromQuery] long? after, [FromQuery] int? limit, [FromQuery] string? classification, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 200, 1, 500);
        var rows = await imports.ListRowsAsync(id, Math.Max(0, after ?? 0), pageSize, classification, ct);
        return Ok(rows);
    }

    [Authorize]
    [HttpGet("/api/v1/imports/{id:guid}", Name = "GetImport")]
    public async Task<IActionResult> GetImport([FromRoute] Guid id, CancellationToken ct)
    {
        var result = await imports.GetAsync(id, ct);
        return result is null ? ApiProblem(title: "Import not found", statusCode: StatusCodes.Status404NotFound) : Ok(result);
    }
}
