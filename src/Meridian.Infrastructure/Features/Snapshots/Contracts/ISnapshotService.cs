namespace Meridian.Infrastructure.Features.Snapshots.Contracts;

public interface ISnapshotService
{
    Task<SnapshotCreateResult> CreateAsync(SnapshotRequest request, string actorUserId, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> ListAsync(CancellationToken cancellationToken);
    Task<object?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> ListResultsAsync(Guid id, int offset, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> ListContributionsAsync(Guid id, string countryCode, int afterOrdinal, int limit, CancellationToken cancellationToken);
    Task<SnapshotPublicationResult> PublishAsync(Guid id, int year, int quarter, bool replaceActive, string actorUserId, string correlationId, CancellationToken cancellationToken);
    Task<SnapshotPublicationResult> WithdrawAsync(Guid id, string actorUserId, string correlationId, CancellationToken cancellationToken);
}
