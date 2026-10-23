using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace Meridian.Infrastructure.Storage;

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
