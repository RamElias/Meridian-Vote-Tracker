using System.Security.Claims;
using Meridian.Infrastructure.Features.Snapshots.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Controllers;

[ApiController]
public sealed class SnapshotsController(ISnapshotService snapshots) : ApiControllerBase
{
    [Authorize]
    [HttpPost("/api/v1/snapshots", Name = "CreateSnapshot")]
    public async Task<IActionResult> CreateSnapshot([FromBody] SnapshotRequest request, CancellationToken ct)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await snapshots.CreateAsync(request, actorId, HttpContext.TraceIdentifier, ct);
        if (!result.Created)
        {
            var extensions = new Dictionary<string, object?> { ["votesWithoutPosition"] = result.VotesWithoutPosition, ["unknownVoteIds"] = result.UnknownVoteIds };
            return ApiProblem(title: "Snapshot selection is invalid", detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, extensions: extensions);
        }
        return Accepted($"/api/v1/snapshots/{result.SnapshotId}", new { id = result.SnapshotId, status = "Queued", selectedVoteCount = result.SelectedVoteCount });
    }

    [Authorize]
    [HttpGet("/api/v1/snapshots", Name = "ListSnapshots")]
    public async Task<IActionResult> ListSnapshots(CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 100, [FromQuery] int? year = null, [FromQuery] short? quarter = null, [FromQuery] string? publication = null)
    {
        if (publication is not (null or "Active" or "Withdrawn" or "Superseded" or "Unpublished"))
            return ApiProblem(title: "Invalid publication filter", statusCode: 400);
        if (quarter is < 1 or > 4 || year is < 1900 or > 9999)
            return ApiProblem(title: "Invalid publication period", statusCode: 400);
        return Ok(await snapshots.ListAsync(ct, offset, limit, year, quarter, publication));
    }

    [Authorize]
    [HttpGet("/api/v1/snapshots/{id:guid}", Name = "GetSnapshot")]
    public async Task<IActionResult> GetSnapshot([FromRoute] Guid id, CancellationToken ct)
    {
        var snapshot = await snapshots.GetAsync(id, ct);
        return snapshot is null ? ApiProblem(title: "Snapshot not found", statusCode: StatusCodes.Status404NotFound) : Ok(snapshot);
    }

    [Authorize]
    [HttpGet("/api/v1/snapshots/{id:guid}/results", Name = "ListSnapshotResults")]
    public async Task<IActionResult> ListSnapshotResults([FromRoute] Guid id, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken ct)
    { return Ok(await snapshots.ListResultsAsync(id, offset ?? 0, limit ?? 100, ct)); }

    [Authorize]
    [HttpGet("/api/v1/snapshots/{id:guid}/countries/{countryCode}/contributions", Name = "ListSnapshotContributions")]
    public async Task<IActionResult> ListSnapshotContributions([FromRoute] Guid id, [FromRoute] string countryCode, [FromQuery] int? after, [FromQuery] int? limit, CancellationToken ct)
    { return Ok(await snapshots.ListContributionsAsync(id, countryCode, after ?? 0, limit ?? 100, ct)); }

    [Authorize(Roles = "Admin")]
    [HttpPost("/api/v1/snapshots/{id:guid}/publish", Name = "PublishSnapshot")]
    public async Task<IActionResult> PublishSnapshot([FromRoute] Guid id, [FromBody] PublishSnapshotRequest request, CancellationToken ct)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await snapshots.PublishAsync(id, request.Year, request.Quarter, request.ReplaceActive, actorId, HttpContext.TraceIdentifier, ct);
        if (result.NotFound) return ApiProblem(title: "Snapshot not found or not publishable", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
        if (!result.Applied) return ApiProblem(title: "Publication conflict", detail: result.Error, statusCode: StatusCodes.Status409Conflict);
        return Ok(new { id, publicationYear = request.Year, publicationQuarter = request.Quarter, status = "Published" });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("/api/v1/snapshots/{id:guid}/withdraw", Name = "WithdrawSnapshot")]
    public async Task<IActionResult> WithdrawSnapshot([FromRoute] Guid id, CancellationToken ct)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await snapshots.WithdrawAsync(id, actorId, HttpContext.TraceIdentifier, ct);
        if (result.NotFound) return ApiProblem(title: "Snapshot is not actively published", statusCode: StatusCodes.Status404NotFound);
        return Ok(new { id, status = "Withdrawn" });
    }
}
