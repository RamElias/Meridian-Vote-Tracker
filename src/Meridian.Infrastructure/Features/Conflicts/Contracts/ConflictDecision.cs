using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Conflicts.Contracts;

public sealed record ConflictDecision(string Action, int? ProposalIndex, string? Note);
