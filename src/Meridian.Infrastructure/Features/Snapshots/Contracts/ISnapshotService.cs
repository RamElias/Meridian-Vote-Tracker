namespace Meridian.Infrastructure.Features.Snapshots.Contracts;

public interface ISnapshotService
{
    Task<SnapshotCreateResult> CreateAsync(SnapshotRequest request, string actorUserId, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SnapshotSummaryResponse>> ListAsync(CancellationToken cancellationToken, int offset = 0, int limit = 100, int? year = null, short? quarter = null, string? publication = null);
    Task<SnapshotDetailResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<SnapshotResultResponse>> ListResultsAsync(Guid id, int offset, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<SnapshotContributionResponse>> ListContributionsAsync(Guid id, string countryCode, int afterOrdinal, int limit, CancellationToken cancellationToken);
    Task<SnapshotPublicationResult> PublishAsync(Guid id, int year, int quarter, bool replaceActive, string actorUserId, string correlationId, CancellationToken cancellationToken);
    Task<SnapshotPublicationResult> WithdrawAsync(Guid id, string actorUserId, string correlationId, CancellationToken cancellationToken);
}
