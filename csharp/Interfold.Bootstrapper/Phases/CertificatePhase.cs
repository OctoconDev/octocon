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

/// <summary>
/// Phase 4 — generates a root CA and a leaf TLS certificate (with SANs) for the configured hosts,
/// emits <c>.crt</c>/<c>.key</c>/<c>.pfx</c> files, and installs the root CA into the system trust
/// store. Hosts can be DNS names, IPv4 / IPv6 literals, or CIDR blocks (the latter restrict the
/// root CA's Name Constraints but never appear as leaf SAN entries). All-C# port of
/// <c>scripts/create-certs.sh</c>.
/// </summary>
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

        // Idempotency contract: the original four-file presence check stays the trigger for
        // "skip the regeneration"; the new rootCA.sha256.txt is treated as derived metadata
        // that EnsureUpgradeArtefacts can backfill in-place. Older installs that predate the
        // trust-distribution work upgrade without forcing --rotate-certs.
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

        // Parse once into the structured HostEntry view; ConfigPhase.Validate has already run
        // HostParser.Parse on every entry by the time we get here, so reparsing here is purely
        // about producing the typed shape the helpers below pattern-match on.
        var hosts = config.Deployment.Hosts.Select(HostParser.Parse).ToList();
        var (rootCert, rootKey) = GenerateRootCa(config.Deployment.RootCaName, config.Deployment.CertYears, hosts);
        var (leafCert, leafKey) = GenerateLeaf(rootCert, rootKey, hosts, config.Deployment.CertYears);

        await PersistAsync(rootCert, rootKey, leafCert, leafKey, secrets.LeafPfxPassword,
            rootCrtPath, rootKeyPath, leafCrtPath, leafKeyPath, leafPfxPath, ct).ConfigureAwait(false);

        WriteFingerprintFile(rootFingerprintPath, rootCert);

        // The published API container reads `/certs/leaf.pfx` through a read-only bind mount as a
        // non-root user (the .NET SDK container builds default to UID 64198). Without world-read
        // bits the container process gets EACCES on startup, so we mark the bind-mounted files
        // 0644. The PFX is password-protected; rootCA.key never leaves the host and is locked
        // down to 0600 below as defence-in-depth against an API-side path-traversal regression
        // (TrustController only serves the public artefacts by explicit allowlist).
        ChmodReadable(leafPfxPath, logger);
        ChmodReadable(leafCrtPath, logger);
        ChmodReadable(rootCrtPath, logger);
        ChmodReadable(rootFingerprintPath, logger);
        ChmodOwnerOnly(rootKeyPath, logger);

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

        // Operator-facing warning: rotating an already-distributed CA invalidates trust on every
        // client device. The fingerprint above is what users compare their re-downloaded copy
        // against; surface the consequence prominently rather than buried in docs.
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

        // CA with pathlen:0 — can sign leaves but not intermediates.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        // Name Constraints (RFC 5280 §4.2.1.10). MUST be critical. Limits this CA to issuing
        // leaves whose dNSName / iPAddress SANs sit within the configured deployment hosts.
        // Blast-radius cap: even a full leak of rootCA.key cannot mint a trusted cert for
        // google.com, 8.8.8.8, etc. — every device that installed this root rejects out-of-
        // permitted names at chain validation. CIDR-shaped hosts widen the permitted iPAddress
        // subtree (operator-scoped to e.g. their LAN) without putting the network range itself
        // on any leaf SAN. Compatibility trade-off: rejects on Android <7, Java <8u101, and a
        // handful of embedded TLS stacks; modern curl / browsers / OpenSSL / .NET handle it
        // cleanly.
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
        // CN := first leaf-eligible (non-CIDR) host. ConfigPhase.Validate guarantees at least
        // one such entry exists, so PickPrimary will never return null at this layer.
        var primary = HostParser.PickPrimary(hosts)
                      ?? throw new InvalidOperationException(
                          "GenerateLeaf requires at least one non-CIDR host - ConfigPhase.Validate " +
                          "should have rejected an all-CIDR list before this point.");
        var primaryCn = primary.Kind switch
        {
            HostKind.Dns => primary.DnsName!,
            // IPv6 in CN is legal as a literal address string; .NET's X500DistinguishedName parser
            // accepts both forms. We use the unbracketed canonical form to avoid surprising tools
            // that diff against the underlying RFC 4514 string representation.
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
                // CIDR entries don't belong on the leaf SAN — a leaf cert can only serve a
                // specific host. They still appear in the root-CA Name Constraints subtree
                // emitted above, which is where they belong.
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
        // The leaf's lifetime cannot extend past the issuer's. The root CA was generated a few
        // microseconds-to-seconds earlier, so its NotAfter is already slightly before
        // `requestedNotAfter`; X509 issuance rejects the cert if we don't clamp.
        // Subtract one second from the issuer cap to leave a definite ordering even when the
        // platform truncates fractional seconds at PEM export time.
        var issuerCap = new DateTimeOffset(issuer.NotAfter.ToUniversalTime()).AddSeconds(-1);
        var notAfter = requestedNotAfter > issuerCap ? issuerCap : requestedNotAfter;

        // CertificateRequest.Create needs a random serial number; use 16 cryptographic bytes.
        var serial = RandomNumberGenerator.GetBytes(16);
        // X.509 serial numbers are unsigned but the high bit must be 0 to keep the DER encoding positive.
        serial[0] &= 0x7F;

        var signed = req.Create(issuer, notBefore, notAfter, serial);
        // The cert returned by Create() doesn't carry the private key; attach it for export.
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

        // Single-cert PFX: leaf certificate + its RSA private key only. X509Certificate2.Export
        // does not walk the issuer chain, so the root CA is deliberately NOT bundled here -
        // clients fetch the root out-of-band via TrustController (/.well-known/interfold-root-ca.crt)
        // and verify it against the operator-distributed SHA-256 fingerprint. The MAC + encryption
        // algorithms come from the .NET runtime defaults (AES-256-CBC + HMAC-SHA-256 on the
        // OpenSSL backend used in the API container); modern Kestrel + clients accept this happily.
        var pfxBytes = leafCert.Export(X509ContentType.Pfx, pfxPassword);
        await File.WriteAllBytesAsync(leafPfxPath, pfxBytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 0644 - readable by any UID, writable only by owner. Mirrors <c>SecretsPhase.ChmodReadable</c>.
    /// Used for cert files that are bind-mounted into the API container, which runs as a
    /// non-root user and cannot read the bootstrapper's default 0600 files.
    /// </summary>
    private static void ChmodReadable(string path, PhaseLogger logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        const int s_644 = 0x1A4; // 0o644
        var rc = NativeMethods.chmod(path, s_644);
        if (rc != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            logger.Warn($"chmod({path}, 0644) failed: errno={err} (cert file written but permissions not adjusted)");
        }
    }

    /// <summary>
    /// 0600 - readable / writable only by the file owner. Applied to <c>rootCA.key</c> so a
    /// shell user other than the bootstrapper invoker (and the non-root API container UID
    /// 64198 that bind-mounts the same directory read-only) cannot read the CA private key.
    /// The host umask typically lands at 0644 which is too permissive for signing material.
    /// </summary>
    internal static void ChmodOwnerOnly(string path, PhaseLogger logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        const int s_600 = 0x180; // 0o600
        var rc = NativeMethods.chmod(path, s_600);
        if (rc != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            logger.Warn($"chmod({path}, 0600) failed: errno={err} (key file written but permissions not adjusted)");
        }
    }

    /// <summary>
    /// Encodes an X.509 v3 Name Constraints extension (OID 2.5.29.30, critical) with one
    /// permittedSubtrees entry per supplied host. No <c>excludedSubtrees</c>: RFC 5280
    /// §4.2.1.10 treats names outside the permitted set as implicitly denied.
    /// <list type="bullet">
    ///   <item><b>DNS</b> hosts emit a <c>dNSName [2] IMPLICIT IA5String</c>. Wildcards
    ///         (<c>*.example.com</c>) collapse to their suffix because dNSName subtree
    ///         semantics already match every sub-label of the suffix and the literal <c>*</c>
    ///         character is not a valid IA5String constraint value.</item>
    ///   <item><b>IP / CIDR</b> hosts emit an <c>iPAddress [7] IMPLICIT OCTET STRING</c>
    ///         whose payload is <c>address || mask</c> in network-byte-order (8 octets for
    ///         IPv4, 32 octets for IPv6). Single-IP hosts use the all-ones mask
    ///         (<c>/32</c> / <c>/128</c>); CIDR hosts use the operator-supplied prefix.</item>
    /// </list>
    /// ASN.1 shape:
    /// <code>
    /// NameConstraints ::= SEQUENCE { permittedSubtrees [0] IMPLICIT GeneralSubtrees }
    /// GeneralSubtrees ::= SEQUENCE OF GeneralSubtree
    /// GeneralSubtree ::= SEQUENCE { base GeneralName }
    /// GeneralName ::= CHOICE { dNSName [2] IMPLICIT IA5String, iPAddress [7] IMPLICIT OCTET STRING, ... }
    /// </code>
    /// </summary>
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

    /// <summary>
    /// Strips a leading <c>*.</c> from a wildcard domain so the result is a valid dNSName for
    /// a Name Constraints permittedSubtree entry. RFC 5280 dNSName subtrees match every host
    /// under the suffix (e.g. <c>example.com</c> permits <c>api.example.com</c> and
    /// <c>admin.example.com</c>) without needing the literal wildcard character — which is
    /// not a legal IA5String constraint value.
    /// </summary>
    internal static string StripWildcardPrefix(string domain) =>
        domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;

    /// <summary>
    /// Persists <c>SHA-256(rootCert.RawData)</c> formatted as colon-separated uppercase hex
    /// (matching the output of <c>openssl x509 -fingerprint -sha256</c>) to
    /// <paramref name="path"/>. The file is the API container's source for the
    /// <c>/.well-known/interfold-root-ca.sha256</c> response and the runtime ETag for the
    /// cert routes — rotating the CA changes the file contents which invalidates downstream
    /// caches automatically.
    /// </summary>
    internal static void WriteFingerprintFile(string path, X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        var formatted = FormatSha256Fingerprint(hash);
        File.WriteAllText(path, formatted + Environment.NewLine);
    }

    /// <summary>
    /// Formats a SHA-256 byte hash as the canonical colon-separated uppercase hex string
    /// (<c>AA:BB:CC:...</c>). Matches the format that <c>openssl x509 -fingerprint -sha256</c>
    /// emits and that browsers show in cert info dialogs, so OOB verification by a human is
    /// a straight character-by-character compare.
    /// </summary>
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

    /// <summary>
    /// Emits the operator-facing trust-distribution block: CA name, on-disk path, SHA-256
    /// fingerprint, expiry, and a copy-pasteable distribute/verify recipe. Called from
    /// <see cref="RunAsync"/> at the end of certificate generation (always; covers the
    /// skip-because-already-present branch via <see cref="EnsureUpgradeArtefacts"/>) and
    /// from <c>show-trust</c> for read-only re-prints after the fact.
    /// </summary>
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

        // Cert Subject is "CN=Interfold Root CA, ..."; render just the CN for friendlier output.
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

    /// <summary>
    /// Brings an older install up to the current on-disk contract without forcing the
    /// operator to run <c>--rotate-certs</c>: backfills the SHA-256 fingerprint file when
    /// missing, and tightens <c>rootCA.key</c> permissions to 0600 if the previous bootstrap
    /// (which only relied on the process umask) left it world-readable. Both operations are
    /// idempotent and safe to re-run.
    /// </summary>
    internal static void EnsureUpgradeArtefacts(string rootCrtPath, string rootKeyPath, string rootFingerprintPath, PhaseLogger logger)
    {
        if (!File.Exists(rootFingerprintPath) && File.Exists(rootCrtPath))
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(rootCrtPath);
            WriteFingerprintFile(rootFingerprintPath, cert);
            ChmodReadable(rootFingerprintPath, logger);
            logger.Info($"    fingerprint backfilled at {rootFingerprintPath}");
        }

        if (File.Exists(rootKeyPath))
        {
            ChmodOwnerOnly(rootKeyPath, logger);
        }
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int chmod(string path, int mode);
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
