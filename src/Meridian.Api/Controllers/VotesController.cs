using System.Security.Claims;
using Meridian.Infrastructure.Features.Votes.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Controllers;

[ApiController]
public sealed class VotesController(IVoteService votes) : ApiControllerBase
{
    [Authorize]
    [HttpGet("/api/v1/votes", Name = "ListVotes")]
    public async Task<IActionResult> ListVotes([FromQuery] string? search, [FromQuery] int? session, [FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken ct)
    { return Ok(await votes.ListAsync(search, session, fromDate, toDate, offset ?? 0, limit ?? 100, ct)); }

    [Authorize]
    [HttpGet("/api/v1/vote", Name = "GetVote")]
    public async Task<IActionResult> GetVote([FromQuery] string voteId, CancellationToken ct)
    {
        var vote = await votes.GetAsync(voteId, ct);
        return vote is null ? ApiProblem(title: "Vote not found", statusCode: StatusCodes.Status404NotFound) : Ok(vote);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("/api/v1/vote/position", Name = "SetVotePosition")]
    public async Task<IActionResult> SetVotePosition([FromQuery] string voteId, [FromBody] PositionUpdateRequest request, CancellationToken ct)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await votes.SetPositionAsync(voteId, request, actorId, HttpContext.TraceIdentifier, ct);
        if (result.NotFound) return ApiProblem(title: "Vote not found", statusCode: StatusCodes.Status404NotFound);
        if (result.Stale) return ApiProblem(title: "Position changed", detail: result.Error, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["currentPosition"] = result.Position });
        if (!result.Updated) return ApiProblem(title: "Invalid position", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
        return Ok(result.Position);
    }
}
