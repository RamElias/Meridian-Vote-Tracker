using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Meridian.Infrastructure.Configuration;

public static class KeyVaultConfigurationExtensions
{
    public static void AddKeyVaultIfConfigured(this IConfigurationBuilder configurationBuilder, IConfiguration configuration)
    {
        var vaultUriValue = configuration["Azure:KeyVault:VaultUri"];
        if (string.IsNullOrWhiteSpace(vaultUriValue))
            return;

        if (!Uri.TryCreate(vaultUriValue, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Azure:KeyVault:VaultUri must be an absolute HTTPS URI.");

        configurationBuilder.AddAzureKeyVault(vaultUri, new DefaultAzureCredential());
    }
}
