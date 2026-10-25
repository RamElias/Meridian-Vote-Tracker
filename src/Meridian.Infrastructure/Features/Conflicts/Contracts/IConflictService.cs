namespace Meridian.Infrastructure.Features.Conflicts.Contracts;

public interface IConflictService
{
    Task<IReadOnlyList<ConflictResponse>> ListPendingAsync(string? voteId, string status, CancellationToken cancellationToken);
    Task<ConflictDecisionResult> DecideAsync(Guid conflictId, ConflictDecision decision, string actorUserId, string correlationId, CancellationToken cancellationToken);
}
