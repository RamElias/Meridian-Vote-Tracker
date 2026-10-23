using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Snapshots.Contracts;

public sealed record SnapshotRequest(int? Session, DateOnly? FromDate, DateOnly? ToDate, IReadOnlyList<string>? VoteIds);
