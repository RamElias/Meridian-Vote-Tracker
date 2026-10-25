namespace Meridian.Infrastructure.Features.Votes.Contracts;

public interface IVoteService
{
    Task<IReadOnlyList<VoteSummaryResponse>> ListAsync(string? search, int? session, DateOnly? fromDate, DateOnly? toDate, int offset, int limit, CancellationToken cancellationToken);
    Task<VoteDetailResponse?> GetAsync(string voteId, CancellationToken cancellationToken);
    Task<PositionWriteResult> SetPositionAsync(string voteId, PositionUpdateRequest request, string actorUserId, string correlationId, CancellationToken cancellationToken);
}
