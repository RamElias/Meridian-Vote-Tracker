using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Votes.Contracts;

public sealed record PositionWriteResult(bool Updated, bool NotFound, string? Error, object? Position, bool Stale = false);
