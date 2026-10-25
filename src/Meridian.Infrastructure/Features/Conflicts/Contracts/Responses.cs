using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Conflicts.Contracts;

public sealed record ConflictResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("targetType")] public required string TargetType { get; init; }
    [JsonPropertyName("voteId")] public required string VoteId { get; init; }
    [JsonPropertyName("countryCode")] public required string? CountryCode { get; init; }
    [JsonPropertyName("currentValue")] public required JsonElement CurrentValue { get; init; }
    [JsonPropertyName("proposals")] public required JsonElement Proposals { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("currentSource")] public required JsonElement? CurrentSource { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("decisionValue")] public required JsonElement? DecisionValue { get; init; }
    [JsonPropertyName("decidedBy")] public required string? DecidedBy { get; init; }
    [JsonPropertyName("decisionNote")] public required string? DecisionNote { get; init; }
    [JsonPropertyName("decidedAt")] public required DateTimeOffset? DecidedAt { get; init; }
}
