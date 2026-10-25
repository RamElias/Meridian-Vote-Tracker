using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Votes.Contracts;

public sealed record PositionResponse
{
    [JsonPropertyName("versionId")] public required long VersionId { get; init; }
    [JsonPropertyName("stance")] public required string Stance { get; init; }
    [JsonPropertyName("weight")] public required short Weight { get; init; }
    [JsonPropertyName("updatedBy")] public required string UpdatedBy { get; init; }
    [JsonPropertyName("note")] public required string? Note { get; init; }
    [JsonPropertyName("updatedAt")] public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record VoteSummaryResponse
{
    [JsonPropertyName("voteId")] public required string VoteId { get; init; }
    [JsonPropertyName("session")] public required int Session { get; init; }
    [JsonPropertyName("voteDate")] public required DateOnly VoteDate { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("stance")] public required string? Stance { get; init; }
    [JsonPropertyName("weight")] public required short? Weight { get; init; }
    [JsonPropertyName("pendingConflicts")] public required long PendingConflicts { get; init; }
}

public sealed record VoteHeaderResponse
{
    [JsonPropertyName("voteId")] public required string VoteId { get; init; }
    [JsonPropertyName("voteVersionId")] public required long VoteVersionId { get; init; }
    [JsonPropertyName("session")] public required int Session { get; init; }
    [JsonPropertyName("voteDate")] public required DateOnly VoteDate { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("publishedAt")] public required DateTimeOffset PublishedAt { get; init; }
    [JsonPropertyName("position")] public required PositionResponse? Position { get; init; }
    [JsonPropertyName("pendingConflicts")] public required long PendingConflicts { get; init; }
}

public sealed record CountryBallotResponse
{
    [JsonPropertyName("countryCode")] public required string CountryCode { get; init; }
    [JsonPropertyName("countryName")] public required string CountryName { get; init; }
    [JsonPropertyName("ballot")] public required string Ballot { get; init; }
    [JsonPropertyName("versionId")] public required long VersionId { get; init; }
    [JsonPropertyName("sourceImportId")] public required Guid SourceImportId { get; init; }
    [JsonPropertyName("sourceFileName")] public required string SourceFileName { get; init; }
    [JsonPropertyName("uploadedBy")] public required string? UploadedBy { get; init; }
    [JsonPropertyName("uploadedAt")] public required DateTimeOffset UploadedAt { get; init; }
}

public sealed record VoteHistoryResponse
{
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("actor")] public required string Actor { get; init; }
    [JsonPropertyName("oldValue")] public required JsonElement? OldValue { get; init; }
    [JsonPropertyName("newValue")] public required JsonElement? NewValue { get; init; }
    [JsonPropertyName("note")] public required string? Note { get; init; }
    [JsonPropertyName("correlationId")] public required string? CorrelationId { get; init; }
    [JsonPropertyName("occurredAt")] public required DateTimeOffset OccurredAt { get; init; }
    [JsonPropertyName("sourceImportId")] public required Guid? SourceImportId { get; init; }
    [JsonPropertyName("sourceFileName")] public required string? SourceFileName { get; init; }
    [JsonPropertyName("entityKey")] public required string EntityKey { get; init; }
}

public sealed record VoteDetailResponse(
    [property: JsonPropertyName("vote")] VoteHeaderResponse Vote,
    [property: JsonPropertyName("countryVotes")] IReadOnlyList<CountryBallotResponse> CountryVotes,
    [property: JsonPropertyName("history")] IReadOnlyList<VoteHistoryResponse> History);
