using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Votes.Contracts;

public sealed record PositionStateResponse(
    [property: JsonPropertyName("versionId")] long? VersionId,
    [property: JsonPropertyName("stance")] string? Stance,
    [property: JsonPropertyName("weight")] int? Weight,
    [property: JsonPropertyName("voteId")] string? VoteId = null,
    [property: JsonPropertyName("updatedBy")] string? UpdatedBy = null);

public sealed record PositionWriteResult(bool Updated, bool NotFound, string? Error, PositionStateResponse? Position, bool Stale = false);
