namespace Meridian.Infrastructure.Features.Imports.Contracts;

public interface IImportService
{
    Task<CreatedImport> CreateAsync(string fileName, string correlationId, string uploadedByUserId, Stream source, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> ListAsync(int offset, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<object>> ListRowsAsync(Guid id, long afterRow, int limit, string? classification, CancellationToken cancellationToken);
    Task<object?> GetAsync(Guid id, CancellationToken cancellationToken);
}
