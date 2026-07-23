using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>Real X.509 work (RSA root CA + leaf → PEM/PFX), fast enough to unit test.</summary>
public sealed class CertificateGenerationTests
{
    private static BootstrapOptions OptionsFor(string outputDir) => TestSupport.MakeOptions(
        command: BootstrapCommand.Bootstrap,
        outputDir: outputDir,
        skipPrereqs: true,
        nonInteractive: true);

    // trustStoreInstall=false keeps unit tests hermetic; true would shell to
    // update-ca-certificates and trip UnauthorizedAccessException on unprivileged CI.
    // Trust-store install is covered by the Docker integration tests.
    private static (BootstrapConfig Config, GeneratedSecrets Secrets) MakeInputs(
        string? rootCaName = null,
        int? certYears = null,
        IList<string>? hosts = null,
        bool trustStoreInstall = false)
    {
        var config = new BootstrapConfig
        {
            Deployment =
            {
                RootCaName = rootCaName ?? "Interfold Root CA",
                CertYears = certYears ?? 5,
                Hosts = hosts is null ? ["api.example.com"] : [.. hosts],
                TrustStoreInstall = trustStoreInstall,
            },
        };
        return (config, SecretsPhase.Generate());
    }


    private static X509Certificate2 LoadCert(string path) => X509CertificateLoader.LoadCertificateFromFile(path);

    /// <summary>Runs the phase against a scratch dir and returns a disposable artefact
    /// bundle. Named parameters keep call sites intent-first.</summary>
    private static async Task<CertPhaseArtifacts> RunPhaseAsync(
        string? rootCaName = null,
        int? certYears = null,
        IList<string>? hosts = null,
        bool trustStoreInstall = false)
    {
        var scratch = TestSupport.NewScratchDir("interfold-certs");
        var (config, secrets) = MakeInputs(rootCaName, certYears, hosts, trustStoreInstall);
        var options = OptionsFor(scratch.Path);
        var logger = new PhaseLogger(options);
        await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);
        return new CertPhaseArtifacts(scratch, config, secrets, options);
    }

    /// <summary>
    /// Disposable bundle produced by <see cref="RunPhaseAsync"/>: owns the scratch directory,
    /// exposes the phase inputs, and provides <see cref="CertPath"/> conveniences to avoid
    /// rebuilding <c>Path.Combine(scratch.Path, "certs", ...)</c> at every call site.
    /// </summary>
    private sealed record CertPhaseArtifacts(
        TestSupport.ScratchDir Scratch,
        BootstrapConfig Config,
        GeneratedSecrets Secrets,
        BootstrapOptions Options) : IDisposable
    {
        public string CertsDir => Path.Combine(Scratch.Path, "certs");
        public string CertPath(string file) => Path.Combine(CertsDir, file);
        public void Dispose() => Scratch.Dispose();
    }

    [Test]
    public async Task CustomRootCaNameAppearsInCertSubject()
    {
        using var artifacts = await RunPhaseAsync(rootCaName: "Acme Trust Root");

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        await Assert.That(root.Subject).Contains("CN=Acme Trust Root");
    }

    [Test]
    public async Task CustomCertYearsControlsNotAfter()
    {
        const int years = 12;
        var before = DateTimeOffset.UtcNow;
        using var artifacts = await RunPhaseAsync(certYears: years);
        var after = DateTimeOffset.UtcNow;

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        var notAfter = root.NotAfter.ToUniversalTime();

        // notAfter is approximately (notBefore + years). notBefore is constructed as
        // (UtcNow - 5min) at phase invocation time, so notAfter ~= before + years - small
        // window. Allow a 2-minute slop on either side to keep the test stable on slow CI.
        var expectedMin = before.AddYears(years).AddMinutes(-10);
        var expectedMax = after.AddYears(years);
        await Assert.That(notAfter).IsGreaterThanOrEqualTo(expectedMin.UtcDateTime);
        await Assert.That(notAfter).IsLessThanOrEqualTo(expectedMax.UtcDateTime);
    }

    [Test]
    public async Task LeafIsSignedByRoot()
    {
        using var artifacts = await RunPhaseAsync();

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        using var leaf = LoadCert(artifacts.CertPath("leaf.crt"));

        // Build a chain anchored on the generated root and verify it.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // The leaf is self-signed by our private root CA; the OS trust store has no opinion on it.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        var built = chain.Build(leaf);
        // Surface the status flags in the failure message if the build fails — useful for diagnosing
        // cert-extension regressions (e.g. someone removes the AKI extension).
        var statuses = chain.ChainStatus.Length == 0
            ? "<none>"
            : string.Join(", ", chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
        await Assert.That(built).IsTrue().Because($"leaf->root chain build failed: {statuses}");

        // Strand 2: leaf SAN is inside the root's permittedSubtrees by construction, so
        // none of the NameConstraints status flags should fire. Asserting their absence
        // is the cheap canary for regressions in BuildNameConstraintsExtension (wrong
        // OID, missing IMPLICIT tag, off-by-one in length prefixes, etc.) — those would
        // typically surface as InvalidNameConstraints / HasNotSupportedNameConstraint
        // even on a within-permitted leaf.
        var ncFlagFired = chain.ChainStatus.Any(s =>
            s.Status == X509ChainStatusFlags.HasNotPermittedNameConstraint
            || s.Status == X509ChainStatusFlags.HasExcludedNameConstraint
            || s.Status == X509ChainStatusFlags.InvalidNameConstraints
            || s.Status == X509ChainStatusFlags.HasNotSupportedNameConstraint);
        await Assert.That(ncFlagFired).IsFalse()
            .Because($"leaf is within permittedSubtrees; no NameConstraints flag should fire. statuses: {statuses}");
    }

    [Test]
    public async Task MultipleHostsBecomeMultipleSans()
    {
        using var artifacts = await RunPhaseAsync(hosts:
            ["api.example.com", "admin.example.com", "www.example.com"]);

        using var leaf = LoadCert(artifacts.CertPath("leaf.crt"));
        // SAN extension OID: 2.5.29.17.
        var san = leaf.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        await Assert.That(san).IsNotNull();

        // Format() returns a human-readable representation that includes each DNS name.
        var sanText = san!.Format(multiLine: true);
        await Assert.That(sanText).Contains("api.example.com");
        await Assert.That(sanText).Contains("admin.example.com");
        await Assert.That(sanText).Contains("www.example.com");
    }

    [Test]
    public async Task TrustStoreInstallFalseProducesArtifactsWithoutErroring()
    {
        // With trustStoreInstall=false the phase must still produce a full set of cert artifacts
        // (root/leaf .crt, leaf.key, leaf.pfx) — it just skips the system trust-store install
        // step. This unit test covers the branch; the integration tests verify that no anchors
        // appear under /usr/local/share/ca-certificates on Linux.
        using var artifacts = await RunPhaseAsync(trustStoreInstall: false);

        // The artifacts must still exist — only the trust-store install is gated on the flag.
        await Assert.That(File.Exists(artifacts.CertPath("rootCA.crt"))).IsTrue();
        await Assert.That(File.Exists(artifacts.CertPath("leaf.crt"))).IsTrue();
        await Assert.That(File.Exists(artifacts.CertPath("leaf.key"))).IsTrue();
        await Assert.That(File.Exists(artifacts.CertPath("leaf.pfx"))).IsTrue();
    }

    [Test]
    public async Task RootCaKeyIsModeSixHundred()
    {
        // Linux-only. .NET's File.WriteAllText honours umask (typically 0644), which would leave
        // the CA private key world-readable. CertificatePhase explicitly chmods rootCA.key to
        // 0600 after PersistAsync so a non-bootstrapper UID on the host can't read it, and the
        // non-root API container UID (64198) that bind-mounts the same dir RO gets EACCES.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        using var artifacts = await RunPhaseAsync();

        var rootKeyPath = artifacts.CertPath("rootCA.key");
        await Assert.That(File.Exists(rootKeyPath)).IsTrue();

        var actualMode = File.GetUnixFileMode(rootKeyPath);
        const UnixFileMode expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await Assert.That(actualMode).IsEqualTo(expectedMode)
            .Because($"rootCA.key must be 0600 (User R+W only); got {actualMode}");
    }

    [Test]
    public async Task RootHasCriticalNameConstraints()
    {
        using var artifacts = await RunPhaseAsync(hosts: ["api.example.com"]);

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        var nc = root.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.30");
        await Assert.That(nc).IsNotNull()
            .Because("Name Constraints extension (OID 2.5.29.30) must be present on the root CA");
        await Assert.That(nc!.Critical).IsTrue()
            .Because("Name Constraints MUST be critical per RFC 5280 §4.2.1.10");
    }

    [Test]
    public async Task PermittedSubtreesMatchHosts()
    {
        var hosts = new List<string> { "api.example.com", "admin.example.com", "www.example.com" };
        using var artifacts = await RunPhaseAsync(hosts: hosts);

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        var nc = root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");

        var parsed = ParseDnsPermittedSubtrees(nc.RawData);
        await Assert.That(parsed).IsEquivalentTo(hosts)
            .Because($"permittedSubtrees must contain exactly the configured hosts; got [{string.Join(", ", parsed)}]");
    }

    [Test]
    public async Task WildcardDomainsStripStarPrefix()
    {
        // dNSName subtree semantics (RFC 5280 §4.2.1.10) already match every host under the
        // suffix, and the literal `*` character is not legal in an IA5String constraint value.
        // BuildNameConstraintsExtension must therefore collapse `*.example.com` to
        // `example.com` in the permittedSubtrees set.
        using var artifacts = await RunPhaseAsync(hosts: ["*.example.com"]);

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        var nc = root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");
        var parsed = ParseDnsPermittedSubtrees(nc.RawData);

        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed[0]).IsEqualTo("example.com");
    }

    [Test]
    public async Task LeafOutsideConstraintsFailsValidation()
    {
        // Negative test: hand-craft a leaf with a SAN outside the root's permittedSubtrees and
        // confirm that chain.Build rejects it with the expected NameConstraints status flag.
        // This is the load-bearing assertion that a leaked CA private key cannot mint a
        // trusted cert for arbitrary hosts.
        using var artifacts = await RunPhaseAsync(hosts: ["api.example.com"]);

        var rootCertPem = await File.ReadAllTextAsync(artifacts.CertPath("rootCA.crt"));
        var rootKeyPem = await File.ReadAllTextAsync(artifacts.CertPath("rootCA.key"));
        using var root = X509Certificate2.CreateFromPem(rootCertPem, rootKeyPem);

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            new X500DistinguishedName("CN=evil.attacker.com"),
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("evil.attacker.com");
        leafReq.CertificateExtensions.Add(sanBuilder.Build());
        leafReq.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;

        // Clamp the leaf strictly inside the root's lifetime so a NotTimeValid status can't
        // mask the NameConstraints violation we're actually testing for.
        var notBefore = new DateTimeOffset(root.NotBefore.ToUniversalTime(), TimeSpan.Zero).AddMinutes(1);
        var notAfter = new DateTimeOffset(root.NotAfter.ToUniversalTime(), TimeSpan.Zero).AddSeconds(-1);
        using var badLeaf = leafReq.Create(root, notBefore, notAfter, serial);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        var built = chain.Build(badLeaf);
        var statuses = chain.ChainStatus.Length == 0
            ? "<none>"
            : string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));

        await Assert.That(built).IsFalse()
            .Because($"chain build must reject a leaf whose SAN is outside the CA's Name Constraints; statuses: {statuses}");

        var ncViolation = chain.ChainStatus.Any(s =>
            s.Status == X509ChainStatusFlags.HasNotPermittedNameConstraint
            || s.Status == X509ChainStatusFlags.HasExcludedNameConstraint
            || s.Status == X509ChainStatusFlags.InvalidNameConstraints
            || s.Status == X509ChainStatusFlags.HasNotSupportedNameConstraint);
        await Assert.That(ncViolation).IsTrue()
            .Because($"chain failed but not for a Name Constraints reason; got: {statuses}");
    }

    [Test]
    public async Task Sha256FingerprintFormatIsColonHexUpperCase()
    {
        // Pure unit test of the FormatSha256Fingerprint helper against a fixed input. SHA-256
        // of an empty byte array is the canonical empty-hash value documented in NIST FIPS
        // 180-4 test vectors. The formatted output must match `openssl dgst -sha256` style
        // exactly (uppercase, colon-separated) so a human comparing fingerprints sees the
        // same characters in both tools.
        var hash = SHA256.HashData(Array.Empty<byte>());
        var formatted = CertificatePhase.FormatSha256Fingerprint(hash);

        await Assert.That(formatted).IsEqualTo(
            "E3:B0:C4:42:98:FC:1C:14:9A:FB:F4:C8:99:6F:B9:24:27:AE:41:E4:64:9B:93:4C:A4:95:99:1B:78:52:B8:55");
    }

    [Test]
    public async Task FingerprintFileWrittenInColonHexUpperCase()
    {
        // End-to-end check on the new rootCA.sha256.txt artefact: it must exist next to
        // rootCA.crt after the certs phase runs, hold the canonical colon-hex SHA-256 of the
        // root cert's DER bytes, and match what `FormatSha256Fingerprint(SHA256.HashData(...))`
        // would compute live. PrintTrustInfo / TrustController both depend on this format.
        using var artifacts = await RunPhaseAsync();

        var fingerprintPath = artifacts.CertPath("rootCA.sha256.txt");
        await Assert.That(File.Exists(fingerprintPath)).IsTrue()
            .Because("rootCA.sha256.txt must be written alongside rootCA.crt");

        var fingerprint = (await File.ReadAllTextAsync(fingerprintPath)).Trim();

        // 32 hex pairs + 31 colon separators = 95 characters.
        await Assert.That(fingerprint.Length).IsEqualTo(95)
            .Because($"colon-hex SHA-256 must be 95 chars; got '{fingerprint}' (len={fingerprint.Length})");
        await Assert.That(Regex.IsMatch(fingerprint, "^([0-9A-F]{2}:){31}[0-9A-F]{2}$")).IsTrue()
            .Because($"fingerprint must match the uppercase colon-hex pattern; got: '{fingerprint}'");

        using var cert = LoadCert(artifacts.CertPath("rootCA.crt"));
        var expected = CertificatePhase.FormatSha256Fingerprint(SHA256.HashData(cert.RawData));
        await Assert.That(fingerprint).IsEqualTo(expected)
            .Because("the file contents must equal the live hash of the published root CA cert");
    }

    [Test]
    public async Task LeafSanIncludesIPv4AsIpAddress()
    {
        // An IPv4 host on deployment.hosts must end up as an iPAddress GeneralName on the leaf's
        // SAN extension. Clients hitting `https://192.168.1.42/` need that entry to validate the
        // cert against the bare IP authority.
        using var artifacts = await RunPhaseAsync(hosts: ["192.168.1.42"]);

        using var leaf = LoadCert(artifacts.CertPath("leaf.crt"));
        var sanExt = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().First();
        var ips = sanExt.EnumerateIPAddresses().Select(ip => ip.ToString()).ToList();
        await Assert.That(ips).Contains("192.168.1.42")
            .Because($"leaf SAN must contain an iPAddress entry for the configured IPv4 host; got [{string.Join(", ", ips)}]");
    }

    [Test]
    public async Task LeafSanIncludesIPv6AsIpAddress()
    {
        using var artifacts = await RunPhaseAsync(hosts: ["fe80::1234"]);

        using var leaf = LoadCert(artifacts.CertPath("leaf.crt"));
        var sanExt = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().First();
        var ips = sanExt.EnumerateIPAddresses().Select(ip => ip.ToString()).ToList();
        await Assert.That(ips).Contains("fe80::1234")
            .Because($"leaf SAN must contain an iPAddress entry for the configured IPv6 host; got [{string.Join(", ", ips)}]");
    }

    [Test]
    public async Task CidrEntrySkippedFromLeafSan()
    {
        // CIDR entries widen the root CA's Name Constraints scope but MUST NOT appear on the
        // leaf SAN — a leaf cert can only serve a specific host. The leaf gets the DNS name; the
        // CIDR only shows up in the root's permittedSubtrees (tested separately below).
        using var artifacts = await RunPhaseAsync(hosts: ["api.example.com", "10.0.0.0/8"]);

        using var leaf = LoadCert(artifacts.CertPath("leaf.crt"));
        var sanExt = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().First();
        var dnsNames = sanExt.EnumerateDnsNames().ToList();
        var ips = sanExt.EnumerateIPAddresses().Select(ip => ip.ToString()).ToList();

        await Assert.That(dnsNames).Contains("api.example.com");
        // The CIDR must NOT appear in either category.
        await Assert.That(ips.Count).IsEqualTo(0)
            .Because($"leaf SAN must not contain any iPAddress entry for a CIDR host; got [{string.Join(", ", ips)}]");
        await Assert.That(dnsNames.Any(d => d.Contains('/'))).IsFalse()
            .Because($"leaf SAN must not contain any DNS entry containing '/' (CIDR shape); got [{string.Join(", ", dnsNames)}]");
    }

    [Test]
    public async Task NameConstraintsEncodesIpv4Cidr()
    {
        // The byte layout of an iPAddress permittedSubtree is load-bearing: chain validators on
        // every client check it as `address || mask`. A drift here would silently broaden or
        // narrow the operator's blast-radius cap. Pin the canonical /24 form (192.168.1.0/24)
        // against the expected 8 bytes: 4 addr (C0 A8 01 00) + 4 mask (FF FF FF 00).
        using var artifacts = await RunPhaseAsync(hosts: ["api.example.com", "192.168.1.0/24"]);

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        var nc = root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");
        var ipSubtrees = ParseIpPermittedSubtrees(nc.RawData);

        await Assert.That(ipSubtrees.Count).IsEqualTo(1)
            .Because($"expected exactly one iPAddress permittedSubtree; got {ipSubtrees.Count}");
        await Assert.That(ipSubtrees[0]).IsEquivalentTo(new byte[]
        {
            0xC0, 0xA8, 0x01, 0x00,
            0xFF, 0xFF, 0xFF, 0x00,
        });
    }

    [Test]
    public async Task NameConstraintsEncodesIpv6SingleHostMask()
    {
        // A bare IPv6 literal becomes a single-host iPAddress subtree with the all-ones /128
        // mask (16 addr bytes + 16 mask bytes). Spot-check the boundary bytes for the canonical
        // form rather than asserting the full 32 bytes verbatim.
        using var artifacts = await RunPhaseAsync(hosts: ["api.example.com", "fe80::1"]);

        using var root = LoadCert(artifacts.CertPath("rootCA.crt"));
        var nc = root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");
        var ipSubtrees = ParseIpPermittedSubtrees(nc.RawData);

        await Assert.That(ipSubtrees.Count).IsEqualTo(1);
        var bytes = ipSubtrees[0];
        await Assert.That(bytes.Length).IsEqualTo(32)
            .Because($"IPv6 iPAddress subtree must be 16 (addr) + 16 (mask) = 32 bytes; got {bytes.Length}");
        // fe80::1 = fe 80 00...00 01 — boundary bytes are deterministic.
        await Assert.That(bytes[0]).IsEqualTo((byte)0xFE);
        await Assert.That(bytes[1]).IsEqualTo((byte)0x80);
        await Assert.That(bytes[15]).IsEqualTo((byte)0x01);
        // All 16 mask bytes are 0xFF for a /128 single host.
        for (var i = 16; i < 32; i++)
        {
            await Assert.That(bytes[i]).IsEqualTo((byte)0xFF)
                .Because($"mask byte at index {i} must be 0xFF for a /128 subtree");
        }
    }

    /// <summary>
    /// Parses the DER-encoded Name Constraints extension value (OID 2.5.29.30) and returns
    /// the dNSName entries inside permittedSubtrees. Skips iPAddress entries (handled by
    /// <see cref="ParseIpPermittedSubtrees"/>) so legacy tests that only care about DNS
    /// names continue to pass on mixed-shape configs.
    /// </summary>
    private static List<string> ParseDnsPermittedSubtrees(byte[] rawExtensionValue)
    {
        var reader = new AsnReader(rawExtensionValue, AsnEncodingRules.DER);
        var nameConstraints = reader.ReadSequence();
        var permitted = nameConstraints.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var names = new List<string>();
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            var peeked = subtree.PeekTag();
            if (peeked.TagClass == TagClass.ContextSpecific && peeked.TagValue == 2)
            {
                names.Add(subtree.ReadCharacterString(
                    UniversalTagNumber.IA5String,
                    new Asn1Tag(TagClass.ContextSpecific, 2)));
            }
            else
            {
                _ = subtree.ReadEncodedValue();
            }
        }
        return names;
    }

    /// <summary>
    /// Sibling of <see cref="ParseDnsPermittedSubtrees"/> that pulls out the iPAddress entries
    /// (tag [7] IMPLICIT OCTET STRING; payload is address || mask per RFC 5280 §4.2.1.10). DNS
    /// entries are skipped. Used by the IP / CIDR Name Constraints tests below.
    /// </summary>
    private static List<byte[]> ParseIpPermittedSubtrees(byte[] rawExtensionValue)
    {
        var reader = new AsnReader(rawExtensionValue, AsnEncodingRules.DER);
        var nameConstraints = reader.ReadSequence();
        var permitted = nameConstraints.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var entries = new List<byte[]>();
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            var peeked = subtree.PeekTag();
            if (peeked.TagClass == TagClass.ContextSpecific && peeked.TagValue == 7)
            {
                entries.Add(subtree.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7)));
            }
            else
            {
                _ = subtree.ReadEncodedValue();
            }
        }
        return entries;
    }
}
