using System.Security.Cryptography;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace Meridian.Infrastructure;

public sealed record StoredImportFile(string Key, string Sha256);

public interface IImportFileStorage
{
    Task<StoredImportFile> SaveAsync(Guid importId, Stream source, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
}

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

public static class ImportFileStorageRegistration
{
    public static IImportFileStorage Create(IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"];
        if (string.Equals(provider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            var serviceUri = configuration["Storage:AzureBlob:ServiceUri"];
            var connectionString = configuration["Storage:AzureBlob:ConnectionString"];
            var containerName = configuration["Storage:AzureBlob:Container"] ?? "meridian-imports";
            BlobServiceClient blobService;
            if (!string.IsNullOrWhiteSpace(connectionString))
                blobService = new BlobServiceClient(connectionString);
            else if (Uri.TryCreate(serviceUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                blobService = new BlobServiceClient(uri, new DefaultAzureCredential());
            else
                throw new InvalidOperationException("Storage:AzureBlob requires an HTTPS ServiceUri or a Key Vault ConnectionString.");

            return new AzureBlobImportFileStorage(blobService.GetBlobContainerClient(containerName), Path.GetTempPath());
        }
        var storagePath = configuration["Storage:LocalPath"];
        if (string.IsNullOrWhiteSpace(storagePath)) storagePath = Path.Combine(AppContext.BaseDirectory, "uploads");
        return new LocalImportFileStorage(storagePath);
    }
}
