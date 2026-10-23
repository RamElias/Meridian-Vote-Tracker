using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Snapshots.Contracts;

public sealed record PublishSnapshotRequest(int Year, int Quarter, bool ReplaceActive);
