using System.Security.Cryptography;

namespace Meridian.Infrastructure.Storage;

public sealed class LocalImportFileStorage(string configuredStoragePath) : IImportFileStorage
{
    public async Task<StoredImportFile> SaveAsync(Guid importId, Stream source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(configuredStoragePath);
        var path = Path.Combine(configuredStoragePath, $"{importId:N}.csv");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await source.CopyToAsync(output, cancellationToken);
        await using var stored = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stored, cancellationToken)).ToLowerInvariant();
        return new StoredImportFile(path, hash);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
}

