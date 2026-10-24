namespace Meridian.Infrastructure.Storage;

public interface IImportFileStorage
{
    Task<StoredImportFile> SaveAsync(Guid importId, Stream source, CancellationToken cancellationToken, string extension = ".csv");
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
}

