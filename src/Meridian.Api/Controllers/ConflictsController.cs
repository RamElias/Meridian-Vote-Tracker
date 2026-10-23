using System.Security.Claims;
using Meridian.Infrastructure.Features.Conflicts.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Controllers;

[ApiController]
public sealed class ConflictsController(IConflictService conflicts) : ApiControllerBase
{
    [Authorize]
    [HttpGet("/api/v1/conflicts", Name = "ListPendingConflicts")]
    public async Task<IActionResult> ListPendingConflicts([FromQuery] string? voteId, [FromQuery] string? status, CancellationToken ct)
    {
        var requestedStatus = string.IsNullOrWhiteSpace(status) ? "Pending" : status.Trim();
        if (requestedStatus is not ("Pending" or "Resolved" or "Accepted" or "Rejected" or "All"))
            return ApiProblem(title: "Invalid conflict status", detail: "Use Pending, Resolved, Accepted, Rejected, or All.", statusCode: StatusCodes.Status400BadRequest);
        return Ok(await conflicts.ListPendingAsync(voteId?.Trim(), requestedStatus, ct));
    }

    [Authorize]
    [HttpPost("/api/v1/conflicts/{id:guid}/decision", Name = "DecideConflict")]
    public async Task<IActionResult> DecideConflict([FromRoute] Guid id, [FromBody] ConflictDecision decision, CancellationToken ct)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await conflicts.DecideAsync(id, decision, actorId, HttpContext.TraceIdentifier, ct);
        if (result.Applied) return Ok(new { id, status = decision.Action.Equals("Accept", StringComparison.OrdinalIgnoreCase) ? "Accepted" : "Rejected" });
        var changed = result.Message?.StartsWith("The conflict was already resolved", StringComparison.Ordinal) == true || result.Message?.StartsWith("The conflict changed", StringComparison.Ordinal) == true;
        return ApiProblem(title: changed ? "Conflict already resolved" : "Invalid decision", detail: result.Message, statusCode: changed ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
    }
}
