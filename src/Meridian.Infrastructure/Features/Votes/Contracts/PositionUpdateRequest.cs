using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Votes.Contracts;

public sealed record PositionUpdateRequest(string? Stance, int Weight, string? Note, [property: JsonRequired] long? ExpectedVersionId = null);
