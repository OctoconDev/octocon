namespace Interfold.AppHostGraph;

/// <summary>Filesystem wire contract for TLS material. The bootstrapper's CertificatePhase
/// writes the host-side files, the emitted compose bakes the container paths in, and
/// operator runbooks reference them — renaming any of these breaks existing deployments.</summary>
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
