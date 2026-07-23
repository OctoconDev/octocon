using System.Formats.Asn1;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Phase 4 — generates a root CA + leaf TLS cert (with SANs) for the configured
/// hosts, emits <c>.crt</c>/<c>.key</c>/<c>.pfx</c>, and installs the root CA into the OS
/// trust store. DNS names, IPv4/6 literals, and CIDR blocks are all accepted; CIDRs restrict
/// the root CA's Name Constraints but never appear on a leaf SAN. All-C# port of
/// <c>scripts/create-certs.sh</c>.</summary>
internal static partial class CertificatePhase
{
    private const string CertsRelativeDir = "certs";
    private const string DebianAnchorsDir = "/usr/local/share/ca-certificates";
    private const string RedHatAnchorsDir = "/etc/pki/ca-trust/source/anchors";
    private const string TrustAnchorFileName = "interfold-root-ca.crt";

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        const string Phase = "certs";
        logger.PhaseStart(Phase);

        var certsDir = Path.Combine(options.OutputDir, CertsRelativeDir);
        var rootCrtPath = Path.Combine(certsDir, "rootCA.crt");
        var rootKeyPath = Path.Combine(certsDir, "rootCA.key");
        var rootFingerprintPath = Path.Combine(certsDir, "rootCA.sha256.txt");
        var leafCrtPath = Path.Combine(certsDir, "leaf.crt");
        var leafKeyPath = Path.Combine(certsDir, "leaf.key");
        var leafPfxPath = Path.Combine(certsDir, "leaf.pfx");

        // Skip-regen is gated on the original four files only; rootCA.sha256.txt is derived
        // metadata that EnsureUpgradeArtefacts backfills so older installs upgrade cleanly.
        var allPresentBeforeRun = File.Exists(rootCrtPath) && File.Exists(leafCrtPath)
                                  && File.Exists(leafKeyPath) && File.Exists(leafPfxPath);
        if (allPresentBeforeRun && !options.RotateCerts)
        {
            EnsureUpgradeArtefacts(rootCrtPath, rootKeyPath, rootFingerprintPath, logger);
            PrintTrustInfo(rootCrtPath, rootFingerprintPath, logger);
            logger.PhaseSkip(Phase, PhaseFailureReasons.Skip.AlreadyPresent);
            return;
        }

        if (allPresentBeforeRun)
        {
            logger.Info("    --rotate-certs set: regenerating root CA + leaf");
        }

        Directory.CreateDirectory(certsDir);

        // ConfigPhase.Validate already parsed these; re-parse here to hand helpers the typed shape.
        var hosts = config.Deployment.Hosts.Select(HostParser.Parse).ToList();
        var (rootCert, rootKey) = GenerateRootCa(config.Deployment.RootCaName, config.Deployment.CertYears, hosts);
        var (leafCert, leafKey) = GenerateLeaf(rootCert, rootKey, hosts, config.Deployment.CertYears);

        await PersistAsync(rootCert, rootKey, leafCert, leafKey, secrets.LeafPfxPassword,
            rootCrtPath, rootKeyPath, leafCrtPath, leafKeyPath, leafPfxPath, ct).ConfigureAwait(false);

        WriteFingerprintFile(rootFingerprintPath, rootCert);

        // Container process (UID 64198) needs 0644 on bind-mounted files or startup EACCES.
        // PFX is password-protected; rootCA.key never leaves the host → 0600 defence-in-depth.
        UnixFilePermissions.SetWorldReadable(leafPfxPath, logger, "cert file");
        UnixFilePermissions.SetWorldReadable(leafCrtPath, logger, "cert file");
        UnixFilePermissions.SetWorldReadable(rootCrtPath, logger, "cert file");
        UnixFilePermissions.SetWorldReadable(rootFingerprintPath, logger, "cert file");
        UnixFilePermissions.SetOwnerOnly(rootKeyPath, logger, "key file");

        rootCert.Dispose();
        leafCert.Dispose();
        rootKey.Dispose();
        leafKey.Dispose();

        if (config.Deployment.TrustStoreInstall && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await InstallToTrustStoreAsync(rootCrtPath, logger, ct).ConfigureAwait(false);
        }
        else
        {
            logger.Info("    skipping trust-store install (trustStoreInstall=false or non-Linux host)");
        }

        PrintTrustInfo(rootCrtPath, rootFingerprintPath, logger);

        // Rotating an already-distributed CA invalidates trust on every client device.
        if (allPresentBeforeRun && options.RotateCerts)
        {
            logger.Warn("All previously-trusted client devices must re-install the new root CA.");
            logger.Warn("Distribute the SHA-256 above out-of-band so users can verify their download.");
        }

        logger.PhaseDone(Phase);
    }

    private static (X509Certificate2 Cert, RSA Key) GenerateRootCa(string rootCaName, int years, IReadOnlyList<HostEntry> hosts)
    {
        var key = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={rootCaName}");

        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // pathlen:0 — signs leaves but not intermediates.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        // Name Constraints (RFC 5280 §4.2.1.10, must be critical) cap the blast radius even on
        // a full rootCA.key leak — trusted devices reject out-of-permitted names. Compatibility
        // trade-off: fails on Android <7, Java <8u101, and a few embedded TLS stacks.
        req.CertificateExtensions.Add(BuildNameConstraintsExtension(hosts));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(years);
        var cert = req.CreateSelfSigned(notBefore, notAfter);
        return (cert, key);
    }

    private static (X509Certificate2 Cert, RSA Key) GenerateLeaf(
        X509Certificate2 issuer,
        RSA issuerKey,
        IReadOnlyList<HostEntry> hosts,
        int years)
    {
        var key = RSA.Create(2048);
        // CN := first leaf-eligible host. ConfigPhase.Validate rejects an all-CIDR list first.
        var primary = HostParser.PickPrimary(hosts)
                      ?? throw new InvalidOperationException(
                          "GenerateLeaf requires at least one non-CIDR host - ConfigPhase.Validate " +
                          "should have rejected an all-CIDR list before this point.");
        var primaryCn = primary.Kind switch
        {
            HostKind.Dns => primary.DnsName!,
            // Unbracketed IPv6 avoids tools that diff against the raw RFC 4514 string.
            _ => primary.Ip!.ToString(),
        };
        var req = new CertificateRequest(new X500DistinguishedName($"CN={primaryCn}"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // serverAuth
            },
            critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var host in hosts)
        {
            switch (host.Kind)
            {
                case HostKind.Dns:
                    sanBuilder.AddDnsName(host.DnsName!);
                    break;
                case HostKind.Ipv4:
                case HostKind.Ipv6:
                    sanBuilder.AddIpAddress(host.Ip!);
                    break;
                // CIDRs belong only on the root-CA Name Constraints subtree, not on a leaf SAN.
                case HostKind.Ipv4Cidr:
                case HostKind.Ipv6Cidr:
                    continue;
            }
        }
        req.CertificateExtensions.Add(sanBuilder.Build());

        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var requestedNotAfter = notBefore.AddYears(years);
        // Clamp leaf NotAfter to (issuer.NotAfter - 1s) so ordering survives PEM truncation.
        var issuerCap = new DateTimeOffset(issuer.NotAfter.ToUniversalTime()).AddSeconds(-1);
        var notAfter = requestedNotAfter > issuerCap ? issuerCap : requestedNotAfter;

        var serial = RandomNumberGenerator.GetBytes(16);
        // High bit clear keeps the unsigned serial's DER encoding positive.
        serial[0] &= 0x7F;

        var signed = req.Create(issuer, notBefore, notAfter, serial);
        var withKey = signed.CopyWithPrivateKey(key);
        signed.Dispose();
        return (withKey, key);
    }

    private static async Task PersistAsync(
        X509Certificate2 rootCert,
        RSA rootKey,
        X509Certificate2 leafCert,
        RSA leafKey,
        string pfxPassword,
        string rootCrtPath,
        string rootKeyPath,
        string leafCrtPath,
        string leafKeyPath,
        string leafPfxPath,
        CancellationToken ct)
    {
        await File.WriteAllTextAsync(rootCrtPath, rootCert.ExportCertificatePem(), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(rootKeyPath, rootKey.ExportPkcs8PrivateKeyPem(), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(leafCrtPath, leafCert.ExportCertificatePem(), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(leafKeyPath, leafKey.ExportPkcs8PrivateKeyPem(), ct).ConfigureAwait(false);

        // Leaf + key only; the root ships OOB via TrustController against its SHA-256 fingerprint.
        var pfxBytes = leafCert.Export(X509ContentType.Pfx, pfxPassword);
        await File.WriteAllBytesAsync(leafPfxPath, pfxBytes, ct).ConfigureAwait(false);
    }


    /// <summary>Emits X.509 v3 Name Constraints (OID 2.5.29.30, critical) with one
    /// permittedSubtrees entry per host. DNS → <c>dNSName [2] IA5String</c> (wildcards
    /// collapsed to suffix — <c>*</c> is not a valid IA5String constraint). IP/CIDR →
    /// <c>iPAddress [7] OCTET STRING</c> carrying address||mask; single-IP hosts use the
    /// all-ones mask.</summary>
    internal static X509Extension BuildNameConstraintsExtension(IReadOnlyList<HostEntry> hosts)
    {
        if (hosts is null || hosts.Count == 0)
        {
            throw new ArgumentException(
                "Name Constraints require at least one permitted host. ConfigPhase.Validate " +
                "rejects an empty deployment.hosts list before this method runs.",
                nameof(hosts));
        }

        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            // permittedSubtrees [0] IMPLICIT GeneralSubtrees
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                foreach (var host in hosts)
                {
                    using (writer.PushSequence())
                    {
                        switch (host.Kind)
                        {
                            case HostKind.Dns:
                                // dNSName [2] IMPLICIT IA5String
                                writer.WriteCharacterString(
                                    UniversalTagNumber.IA5String,
                                    StripWildcardPrefix(host.DnsName!),
                                    new Asn1Tag(TagClass.ContextSpecific, 2));
                                break;
                            case HostKind.Ipv4:
                            case HostKind.Ipv6:
                            case HostKind.Ipv4Cidr:
                            case HostKind.Ipv6Cidr:
                                // iPAddress [7] IMPLICIT OCTET STRING: payload is address || mask.
                                writer.WriteOctetString(
                                    HostParser.ToNameConstraintSubtreeBytes(host),
                                    new Asn1Tag(TagClass.ContextSpecific, 7));
                                break;
                        }
                    }
                }
            }
        }

        return new X509Extension(new Oid("2.5.29.30"), writer.Encode(), critical: true);
    }

    /// <summary>Strips a leading <c>*.</c> so the result is a valid dNSName constraint;
    /// the suffix subtree already matches every host beneath it.</summary>
    internal static string StripWildcardPrefix(string domain) =>
        domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;

    /// <summary>Writes SHA-256(rootCert.RawData) as colon-separated uppercase hex. Backs the
    /// API's <c>/.well-known/interfold-root-ca.sha256</c> and the cert routes' ETag.</summary>
    internal static void WriteFingerprintFile(string path, X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        var formatted = FormatSha256Fingerprint(hash);
        File.WriteAllText(path, formatted + Environment.NewLine);
    }

    /// <summary>Canonical colon-separated uppercase hex (<c>AA:BB:CC:...</c>) matching
    /// <c>openssl x509 -fingerprint -sha256</c> and browser cert dialogs.</summary>
    internal static string FormatSha256Fingerprint(byte[] hash)
    {
        var sb = new StringBuilder(hash.Length * 3);
        for (var i = 0; i < hash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Operator-facing trust block: CA name, path, SHA-256, expiry, distribute/verify
    /// recipe. Also invoked by <c>show-trust</c>.</summary>
    internal static void PrintTrustInfo(string rootCrtPath, string rootFingerprintPath, PhaseLogger logger)
    {
        if (!File.Exists(rootCrtPath))
        {
            logger.Warn($"trust info: rootCA.crt missing at {rootCrtPath}");
            return;
        }

        using var cert = X509CertificateLoader.LoadCertificateFromFile(rootCrtPath);

        var fingerprint = File.Exists(rootFingerprintPath)
            ? File.ReadAllText(rootFingerprintPath).Trim()
            : FormatSha256Fingerprint(SHA256.HashData(cert.RawData));

        // Render just the CN for friendlier output.
        var caName = cert.Subject;
        if (caName.StartsWith("CN=", StringComparison.Ordinal))
        {
            caName = caName[3..];
            var comma = caName.IndexOf(',');
            if (comma > 0) caName = caName[..comma];
        }

        logger.Info("");
        logger.Info($"    Root CA:     {caName}");
        logger.Info($"      Path:        {rootCrtPath}");
        logger.Info($"      SHA-256:     {fingerprint}");
        logger.Info($"      Not after:   {cert.NotAfter.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
        logger.Info($"      Distribute:  curl -fSL http://<host>:5000/.well-known/interfold-root-ca.crt -o rootCA.crt");
        logger.Info($"      Verify:      openssl x509 -in rootCA.crt -noout -fingerprint -sha256");
        logger.Info($"                   (compare the printed SHA256 Fingerprint to the value above)");
        logger.Info("");
    }

    /// <summary>Upgrades an older install in-place: backfills the SHA-256 fingerprint file and
    /// re-tightens rootCA.key to 0600. Both idempotent.</summary>
    internal static void EnsureUpgradeArtefacts(string rootCrtPath, string rootKeyPath, string rootFingerprintPath, PhaseLogger logger)
    {
        if (!File.Exists(rootFingerprintPath) && File.Exists(rootCrtPath))
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(rootCrtPath);
            WriteFingerprintFile(rootFingerprintPath, cert);
            UnixFilePermissions.SetWorldReadable(rootFingerprintPath, logger, "cert file");
            logger.Info($"    fingerprint backfilled at {rootFingerprintPath}");
        }

        if (File.Exists(rootKeyPath))
        {
            UnixFilePermissions.SetOwnerOnly(rootKeyPath, logger, "key file");
        }
    }

    private static async Task InstallToTrustStoreAsync(string rootCrtPath, PhaseLogger logger, CancellationToken ct)
    {
        if (Directory.Exists(DebianAnchorsDir))
        {
            var dst = Path.Combine(DebianAnchorsDir, TrustAnchorFileName);
            File.Copy(rootCrtPath, dst, overwrite: true);
            var run = await ProcessRunner.RunAsync("update-ca-certificates", [], ct: ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                logger.Warn($"update-ca-certificates exited {run.ExitCode}: {run.StdErr.Trim()}");
            }
            else
            {
                logger.Info("    root CA installed via update-ca-certificates");
            }
        }
        else if (Directory.Exists(RedHatAnchorsDir))
        {
            var dst = Path.Combine(RedHatAnchorsDir, TrustAnchorFileName);
            File.Copy(rootCrtPath, dst, overwrite: true);
            var run = await ProcessRunner.RunAsync("update-ca-trust", ["extract"], ct: ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                logger.Warn($"update-ca-trust extract exited {run.ExitCode}: {run.StdErr.Trim()}");
            }
            else
            {
                logger.Info("    root CA installed via update-ca-trust extract");
            }
        }
        else
        {
            logger.Warn(
                "no known trust-store path found (looked for /usr/local/share/ca-certificates and " +
                "/etc/pki/ca-trust/source/anchors). Install the generated rootCA.crt manually.");
        }
    }
}
