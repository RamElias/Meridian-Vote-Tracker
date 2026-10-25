using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Snapshots.Contracts;

public sealed record SnapshotSummaryResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("ruleVersion")] public required string RuleVersion { get; init; }
    [JsonPropertyName("createdBy")] public required string CreatedBy { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("completedAt")] public required DateTimeOffset? CompletedAt { get; init; }
    [JsonPropertyName("publicationYear")] public required int? PublicationYear { get; init; }
    [JsonPropertyName("publicationQuarter")] public required short? PublicationQuarter { get; init; }
    [JsonPropertyName("publishedAt")] public required DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("withdrawnAt")] public required DateTimeOffset? WithdrawnAt { get; init; }
    [JsonPropertyName("supersededAt")] public required DateTimeOffset? SupersededAt { get; init; }
    [JsonPropertyName("progressCurrent")] public required long ProgressCurrent { get; init; }
    [JsonPropertyName("progressTotal")] public required long? ProgressTotal { get; init; }
    [JsonPropertyName("selectedVoteCount")] public required long SelectedVoteCount { get; init; }
    [JsonPropertyName("error")] public required string? Error { get; init; }
    [JsonPropertyName("progress")] public required JsonElement? Progress { get; init; }
}

public sealed record SnapshotDetailResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("selection")] public required JsonElement Selection { get; init; }
    [JsonPropertyName("ruleVersion")] public required string RuleVersion { get; init; }
    [JsonPropertyName("createdBy")] public required string CreatedBy { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("inputsCapturedAt")] public required DateTimeOffset? InputsCapturedAt { get; init; }
    [JsonPropertyName("completedAt")] public required DateTimeOffset? CompletedAt { get; init; }
    [JsonPropertyName("error")] public required string? Error { get; init; }
    [JsonPropertyName("publicationYear")] public required int? PublicationYear { get; init; }
    [JsonPropertyName("publicationQuarter")] public required short? PublicationQuarter { get; init; }
    [JsonPropertyName("publishedAt")] public required DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("publishedBy")] public required string? PublishedBy { get; init; }
    [JsonPropertyName("withdrawnAt")] public required DateTimeOffset? WithdrawnAt { get; init; }
    [JsonPropertyName("withdrawnBy")] public required string? WithdrawnBy { get; init; }
    [JsonPropertyName("supersededAt")] public required DateTimeOffset? SupersededAt { get; init; }
    [JsonPropertyName("supersededBy")] public required Guid? SupersededBy { get; init; }
    [JsonPropertyName("progressCurrent")] public required long ProgressCurrent { get; init; }
    [JsonPropertyName("progressTotal")] public required long? ProgressTotal { get; init; }
    [JsonPropertyName("selectedVoteCount")] public required long SelectedVoteCount { get; init; }
    [JsonPropertyName("progress")] public required JsonElement? Progress { get; init; }
}

public sealed record SnapshotResultResponse
{
    [JsonPropertyName("countryCode")] public required string CountryCode { get; init; }
    [JsonPropertyName("countryName")] public required string CountryName { get; init; }
    [JsonPropertyName("rank")] public required int? Rank { get; init; }
    [JsonPropertyName("alignmentScore")] public required decimal? AlignmentScore { get; init; }
    [JsonPropertyName("alignmentNumerator")] public required decimal AlignmentNumerator { get; init; }
    [JsonPropertyName("alignmentDenominator")] public required decimal AlignmentDenominator { get; init; }
    [JsonPropertyName("participationPercent")] public required decimal? ParticipationPercent { get; init; }
    [JsonPropertyName("participatingBallots")] public required int ParticipatingBallots { get; init; }
    [JsonPropertyName("approvedBallots")] public required int ApprovedBallots { get; init; }
    [JsonPropertyName("coveragePercent")] public required decimal? CoveragePercent { get; init; }
    [JsonPropertyName("approvedMemberRecords")] public required int ApprovedMemberRecords { get; init; }
    [JsonPropertyName("eligibleMemberVotes")] public required int EligibleMemberVotes { get; init; }
}

public sealed record SnapshotContributionResponse
{
    [JsonPropertyName("ordinal")] public required int Ordinal { get; init; }
    [JsonPropertyName("voteId")] public required string VoteId { get; init; }
    [JsonPropertyName("session")] public required int Session { get; init; }
    [JsonPropertyName("voteDate")] public required DateOnly VoteDate { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("instituteStance")] public required string InstituteStance { get; init; }
    [JsonPropertyName("weight")] public required short Weight { get; init; }
    [JsonPropertyName("memberAtVote")] public required bool MemberAtVote { get; init; }
    [JsonPropertyName("ballot")] public required string? Ballot { get; init; }
    [JsonPropertyName("alignmentNumerator")] public required decimal AlignmentNumerator { get; init; }
    [JsonPropertyName("alignmentDenominator")] public required decimal AlignmentDenominator { get; init; }
    [JsonPropertyName("participationNumerator")] public required int ParticipationNumerator { get; init; }
    [JsonPropertyName("participationDenominator")] public required int ParticipationDenominator { get; init; }
    [JsonPropertyName("coverageNumerator")] public required int CoverageNumerator { get; init; }
    [JsonPropertyName("coverageDenominator")] public required int CoverageDenominator { get; init; }
    [JsonPropertyName("countryVoteVersionId")] public required long? CountryVoteVersionId { get; init; }
}
