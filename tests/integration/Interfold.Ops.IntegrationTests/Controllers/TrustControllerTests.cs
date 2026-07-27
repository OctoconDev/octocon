using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Shared.Contracts;

namespace Interfold.Ops.IntegrationTests.Controllers;

// Each test spins its own factory so the env-bound TrustOptions snapshot (read once at
// startup via IOptions) captures the path shape the case wants.
public class TrustControllerTests : BaseEndpointTest
{
    [Test]
    public async Task ReturnsNotFoundWhenRootCaPathUnset()
    {
        // Dev-mode parity: no /certs bind mount -> no OCTOCON_TRUST_* env vars; contract is
        // 404-on-every-route, not 500-because-IO-failed.
        await using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory);
        using var client = factory.CreateClient();

        foreach (var path in new[]
                 {
                     "/.well-known/interfold-root-ca.crt",
                     "/.well-known/interfold-root-ca.pem",
                     "/.well-known/interfold-root-ca.sha256",
                 })
        {
            var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound)
                .Because($"{path} must 404 when TrustOptions paths are unset");
        }
    }

    [Test]
    public async Task ReturnsCertWithCorrectContentTypeAndBytes()
    {
        await using var h = await NewTrustCertHarnessAsync();

        var crt = await h.Client.GetAsync("/.well-known/interfold-root-ca.crt");
        await Assert.That(crt.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(crt.Content.Headers.ContentType?.MediaType).IsEqualTo("application/pkix-cert");
        var crtBytes = await crt.Content.ReadAsByteArrayAsync();
        await Assert.That(crtBytes).IsEquivalentTo(h.ExpectedDer)
            .Because("the .crt route must serve the cert in DER form");

        var pem = await h.Client.GetAsync("/.well-known/interfold-root-ca.pem");
        await Assert.That(pem.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(pem.Content.Headers.ContentType?.MediaType).IsEqualTo("application/x-pem-file");
        var pemBytes = await pem.Content.ReadAsByteArrayAsync();
        await Assert.That(pemBytes).IsEquivalentTo(await File.ReadAllBytesAsync(h.CertPath))
            .Because("the .pem route must stream the on-disk bytes verbatim");

        var sha256 = await h.Client.GetAsync("/.well-known/interfold-root-ca.sha256");
        await Assert.That(sha256.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sha256.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        var sha256Text = (await sha256.Content.ReadAsStringAsync()).Trim();
        await Assert.That(sha256Text).IsEqualTo(h.ExpectedFingerprint);

        // Safari / iOS profile flow: explicit Accept: application/x-x509-ca-cert gets the
        // legacy type back.
        using var legacyReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/interfold-root-ca.crt");
        legacyReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-x509-ca-cert"));
        var legacy = await h.Client.SendAsync(legacyReq);
        await Assert.That(legacy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(legacy.Content.Headers.ContentType?.MediaType).IsEqualTo("application/x-x509-ca-cert");
    }

    [Test]
    public async Task ReturnsEtagMatchingFingerprintFile()
    {
        // The ETag is the only mechanism that propagates a --rotate-certs through downstream
        // caches before Cache-Control max-age expires. Locking the value to the fingerprint
        // file's contents (verbatim, quoted) protects that contract.
        await using var h = await NewTrustCertHarnessAsync();

        foreach (var path in new[]
                 {
                     "/.well-known/interfold-root-ca.crt",
                     "/.well-known/interfold-root-ca.pem",
                     "/.well-known/interfold-root-ca.sha256",
                 })
        {
            var response = await h.Client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var etag = response.Headers.ETag?.Tag;
            await Assert.That(etag).IsEqualTo($"\"{h.ExpectedFingerprint}\"")
                .Because($"{path} must return ETag derived from rootCA.sha256.txt");

            var cacheControl = response.Headers.CacheControl?.ToString();
            await Assert.That(cacheControl).IsNotNull();
            await Assert.That(cacheControl!).Contains("public");
            await Assert.That(cacheControl!).Contains("max-age=60");
            await Assert.That(cacheControl!).Contains("must-revalidate");
        }
    }

    [Test]
    public async Task HeadRequestsReturnHeadersWithoutBody()
    {
        // [HttpHead] must sit alongside [HttpGet] on each action; otherwise routing rejects
        // HEAD, it falls through to the default authorize policy, and CDN / curl -I
        // revalidation gets a 401 challenge instead of headers.
        await using var h = await NewTrustCertHarnessAsync();

        foreach (var path in new[]
                 {
                     "/.well-known/interfold-root-ca.crt",
                     "/.well-known/interfold-root-ca.pem",
                     "/.well-known/interfold-root-ca.sha256",
                 })
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, path);
            var response = await h.Client.SendAsync(req);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because($"HEAD {path} must return 200 (not 401 / 405) so cache revalidation works");

            var etag = response.Headers.ETag?.Tag;
            await Assert.That(etag).IsEqualTo($"\"{h.ExpectedFingerprint}\"")
                .Because($"HEAD {path} must publish the same ETag as GET so conditional caches stay in sync");

            var body = await response.Content.ReadAsByteArrayAsync();
            await Assert.That(body.Length).IsEqualTo(0)
                .Because($"HEAD {path} must omit the response body (RFC 9110 §9.3.2)");
        }
    }

    [Test]
    public async Task WellKnownRoutesBypassHttpsRedirect()
    {
        // Trust-bootstrap contract: devices fetching the root CA have no HTTPS trust path yet,
        // so /.well-known/* must serve over plain HTTP. HTTPS_PORT=443 activates
        // HttpsRedirectionMiddleware in-test (without it the middleware logs a warning and
        // lets everything through); environment=Production opens the !IsDevelopment() gate in
        // Program.cs that wraps HSTS + HttpsRedirection.
        await using var h = await NewTrustCertHarnessAsync(
            noRedirect: true,
            extraConfig:
            [
                ("HTTPS_PORT", "443"),
                ("environment", "Production"),
            ]);

        foreach (var path in new[]
                 {
                     "/.well-known/interfold-root-ca.crt",
                     "/.well-known/interfold-root-ca.pem",
                     "/.well-known/interfold-root-ca.sha256",
                 })
        {
            var response = await h.Client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because($"{path} must serve the artefact directly over plain HTTP, not redirect to HTTPS");
            await Assert.That((int)response.StatusCode is >= 300 and < 400).IsFalse()
                .Because($"{path} must NOT issue any 3xx redirect - clients fetching the root CA have no HTTPS trust path");
        }

        // Negative control: every other path must still redirect, so the bypass stays
        // narrowly scoped.
        var control = await h.Client.GetAsync("/api/i-do-not-exist");
        await Assert.That((int)control.StatusCode is >= 300 and < 400).IsTrue()
            .Because($"non-.well-known paths must still go through HttpsRedirection (got {(int)control.StatusCode})");
    }

    // Mints a self-signed CA + fingerprint sidecar in a temp dir, wires them onto the factory
    // via OCTOCON_TRUST_ROOT_CA_{PATH,FINGERPRINT_PATH}, and returns a harness that owns
    // teardown. noRedirect: surfaces 3xx responses as-is. extraConfig: extra env-var pairs
    // applied after the two OCTOCON_TRUST_ROOT_CA_* keys.
    private static async Task<TrustCertHarness> NewTrustCertHarnessAsync(
        bool noRedirect = false,
        IReadOnlyList<(string Key, string Value)>? extraConfig = null)
    {
        var (certPath, fingerprintPath, expectedDer, expectedFingerprint, tmpDir) = MintTempCertFiles();

        var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_TRUST_ROOT_CA_PATH", certPath)
            .WithConfiguration("OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH", fingerprintPath);
        if (extraConfig is not null)
        {
            foreach (var (key, value) in extraConfig)
            {
                factory.WithConfiguration(key, value);
            }
        }

        var client = noRedirect ? TestClient.NoRedirect(factory) : factory.CreateClient();

        // Boot the factory's TestServer before the caller starts asserting.
        await Task.Yield();

        return new TrustCertHarness(client, factory, tmpDir,
            certPath, expectedDer, expectedFingerprint);
    }

    // Teardown order matters: client first so no in-flight response outlives the server,
    // then factory (stops the in-memory host), then temp dir (best-effort; OS tmp reaper
    // catches leaks).
    private sealed class TrustCertHarness(
        HttpClient client,
        InterfoldWebApplicationFactory factory,
        string tmpDir,
        string certPath,
        byte[] expectedDer,
        string expectedFingerprint) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public string CertPath { get; } = certPath;
        public byte[] ExpectedDer { get; } = expectedDer;
        public string ExpectedFingerprint { get; } = expectedFingerprint;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync().ConfigureAwait(false);
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Mints rootCA.crt (PEM) + rootCA.sha256.txt (uppercase colon-hex) in a temp dir; returns
    // paths + expected DER so the caller can assert byte-for-byte.
    private static (string certPath, string fingerprintPath, byte[] expectedDer, string expectedFingerprint, string tmpDir) MintTempCertFiles()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-trust-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        using var key = RSA.Create(2048);
        var req = new CertificateRequest(
            new X500DistinguishedName("CN=Test Trust Controller Root"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
        var certPath = Path.Combine(tmpDir, "rootCA.crt");
        File.WriteAllText(certPath, cert.ExportCertificatePem());

        var hash = SHA256.HashData(cert.RawData);
        var fingerprint = ToColonHexUpper(hash);
        var fingerprintPath = Path.Combine(tmpDir, "rootCA.sha256.txt");
        File.WriteAllText(fingerprintPath, fingerprint + Environment.NewLine);

        var expectedDer = (byte[])cert.RawData.Clone();

        return (certPath, fingerprintPath, expectedDer, fingerprint, tmpDir);
    }

    private static string ToColonHexUpper(byte[] hash)
    {
        var sb = new System.Text.StringBuilder(hash.Length * 3);
        for (var i = 0; i < hash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hash[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
