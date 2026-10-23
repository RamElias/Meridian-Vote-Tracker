using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Snapshots.Contracts;

public sealed record SnapshotPublicationResult(bool Applied, string? Error, bool NotFound = false);
