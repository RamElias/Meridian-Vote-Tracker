namespace Meridian.Infrastructure.Storage;

public interface IImportFileStorage
{
    Task<StoredImportFile> SaveAsync(Guid importId, Stream source, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
}

