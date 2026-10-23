using System.Security.Cryptography;
using Azure.Storage.Blobs;

namespace Meridian.Infrastructure.Storage;

public sealed class AzureBlobImportFileStorage(BlobContainerClient container, string temporaryPath) : IImportFileStorage
{
    public async Task<StoredImportFile> SaveAsync(Guid importId, Stream source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(temporaryPath);
        var temporaryFile = Path.Combine(temporaryPath, $"{Guid.NewGuid():N}.csv");
        try
        {
            await using (var output = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(output, cancellationToken);
            string hash;
            await using (var stored = File.OpenRead(temporaryFile))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stored, cancellationToken)).ToLowerInvariant();
            var key = $"imports/{importId:N}.csv";
            await using (var stored = File.OpenRead(temporaryFile))
                await container.GetBlobClient(key).UploadAsync(stored, overwrite: false, cancellationToken);
            return new StoredImportFile(key, hash);
        }
        finally
        {
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var download = await container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: cancellationToken);
        return download.Value.Content;
    }
}

