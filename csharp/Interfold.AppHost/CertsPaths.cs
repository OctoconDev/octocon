namespace Interfold.AppHostGraph;

/// <summary>
/// The TLS-material paths shared between the AppHost graph's bind mounts and the env vars
/// that point containers at files inside those mounts. The host side is where the
/// bootstrapper's CertificatePhase writes its output (relative to the compose file's
/// directory); the container side is the read-only mount target consumed by Kestrel,
/// nginx, and TrustController.
/// </summary>
/// <remarks>
/// These are filesystem wire contracts: the bootstrapper writes the files, the published
/// compose file bakes the paths in, and operator runbooks reference them. Renaming any of
/// them is a breaking change for existing deployments.
/// </remarks>
internal static class CertsPaths
{
    /// <summary>Host-side certs directory, relative to the emitted compose file.</summary>
    public const string HostDir = "../../certs";

    /// <summary>In-container mount point for <see cref="HostDir"/>.</summary>
    public const string ContainerDir = "/certs";

    /// <summary>Leaf PFX consumed by Kestrel as the default HTTPS certificate.</summary>
    public const string LeafPfx = "/certs/leaf.pfx";

    /// <summary>Leaf certificate (PEM) consumed by the nginx TLS server block.</summary>
    public const string LeafCrt = "/certs/leaf.crt";

    /// <summary>Leaf private key (PEM) consumed by the nginx TLS server block.</summary>
    public const string LeafKey = "/certs/leaf.key";

    /// <summary>Root CA certificate served by TrustController's well-known routes.</summary>
    public const string RootCaCrt = "/certs/rootCA.crt";

    /// <summary>Root CA SHA-256 fingerprint file; also sources the ETag on the cert routes.</summary>
    public const string RootCaFingerprint = "/certs/rootCA.sha256.txt";
}
