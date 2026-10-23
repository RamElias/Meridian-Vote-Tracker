using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Conflicts.Contracts;

public sealed record ConflictDecisionResult(bool Applied, string? Message);
