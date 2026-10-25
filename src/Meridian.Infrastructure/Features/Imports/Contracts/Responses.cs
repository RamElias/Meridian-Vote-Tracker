using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Imports.Contracts;

public sealed record ImportSummaryResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("rowsProcessed")] public required long RowsProcessed { get; init; }
    [JsonPropertyName("rowsTotal")] public required long? RowsTotal { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("completedAt")] public required DateTimeOffset? CompletedAt { get; init; }
    [JsonPropertyName("originalImportId")] public required Guid? OriginalImportId { get; init; }
    [JsonPropertyName("uploadedBy")] public required string? UploadedBy { get; init; }
    [JsonPropertyName("progress")] public required JsonElement? Progress { get; init; }
}

public sealed record ImportRowResponse
{
    [JsonPropertyName("ballotClassification")] public required string? BallotClassification { get; init; }
    [JsonPropertyName("metadataConflictId")] public required Guid? MetadataConflictId { get; init; }
    [JsonPropertyName("rowNumber")] public required long RowNumber { get; init; }
    [JsonPropertyName("classification")] public required string? Classification { get; init; }
    [JsonPropertyName("reason")] public required string? Reason { get; init; }
    [JsonPropertyName("values")] public required JsonElement Values { get; init; }
    [JsonPropertyName("normalizedValues")] public required JsonElement NormalizedValues { get; init; }
    [JsonPropertyName("normalizations")] public required JsonElement Normalizations { get; init; }
}

public sealed record ImportDetailResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("rowsProcessed")] public required long RowsProcessed { get; init; }
    [JsonPropertyName("rowsTotal")] public required long? RowsTotal { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("completedAt")] public required DateTimeOffset? CompletedAt { get; init; }
    [JsonPropertyName("originalImportId")] public required Guid? OriginalImportId { get; init; }
    [JsonPropertyName("report")] public required JsonElement? Report { get; init; }
    [JsonPropertyName("uploadedBy")] public required string? UploadedBy { get; init; }
    [JsonPropertyName("progress")] public required JsonElement? Progress { get; init; }
}
