using System.Text.Json.Serialization;

namespace Meridian.Infrastructure.Features.Imports.Contracts;

public sealed record CreatedImport(Guid ImportId, string Status, Guid? OriginalImportId);
