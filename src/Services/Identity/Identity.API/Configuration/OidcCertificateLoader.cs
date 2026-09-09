using System.Security.Cryptography.X509Certificates;

namespace Identity.API.Configuration;

/// <summary>
/// Loads the OpenIddict signing/encryption certificate that MUST be shared with the monolith
/// (same PFX, same password) so tokens issued by either application validate on the other.
/// Configuration keys intentionally mirror the monolith: OIDC:Certificates:Path / OIDC:Certificates:Password.
/// </summary>
public static class OidcCertificateLoader
{
    public const string PathKey = "OIDC:Certificates:Path";
    public const string PasswordKey = "OIDC:Certificates:Password";
    public const string RequireSharedKey = "OIDC:Certificates:RequireShared";

    public static X509Certificate2? Load(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var path = configuration[PathKey];
        var password = configuration[PasswordKey];
        var requireShared = configuration.GetValue(RequireSharedKey, !environment.IsDevelopment());

        if (string.IsNullOrWhiteSpace(path))
        {
            if (requireShared)
            {
                throw new InvalidOperationException(
                    $"'{PathKey}' is not configured. A persisted PFX shared with the monolith is mandatory " +
                    "during the strangler window; refusing to start with per-instance keys. " +
                    $"Set {RequireSharedKey}=false only for isolated development.");
            }

            logger.LogWarning(
                "No shared OIDC certificate configured; falling back to development certificates. " +
                "Tokens issued by this instance will NOT validate on the monolith.");
            return null;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"OIDC certificate '{path}' configured via '{PathKey}' does not exist.", path);

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);

        logger.LogInformation("Loaded shared OIDC certificate {Thumbprint} (expires {NotAfter:u})",
            certificate.Thumbprint, certificate.NotAfter);

        return certificate;
    }
}
