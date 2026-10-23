using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Snapshots.Contracts;

public sealed record SnapshotCreateResult(bool Created, Guid? SnapshotId, string? Error, IReadOnlyList<string>? VotesWithoutPosition, int SelectedVoteCount, IReadOnlyList<string>? UnknownVoteIds = null);
