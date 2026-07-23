using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Interfold.Shared.Contracts;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// HTTP-level coverage of <c>TrustController</c> (<c>/.well-known/interfold-root-ca.*</c>).
/// Each test constructs a fresh factory so the env-bound <see cref="Interfold.Shared.Contracts.Configuration.TrustOptions"/>
/// snapshot — read once at startup via <see cref="Microsoft.Extensions.Options.IOptions{T}"/> —
/// captures the per-test path the case under test wants to exercise. In-memory persistence is
/// used because the trust routes don't touch any persistence surface and the inmemory factory
/// boots in well under a second.
/// </summary>
public class TrustControllerTests : BaseEndpointTest
{
    [Test]
    public async Task ReturnsNotFoundWhenRootCaPathUnset()
    {
        // Dev-mode parity: no /certs bind mount → bootstrapper doesn't emit the OCTOCON_TRUST_*
        // env vars → the controller has nothing to serve. The contract is "404 on every
        // route", not "500 because IO failed", so users running `aspire run` locally just
        // see a clean miss instead of a noisy stack trace.
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
        // Mint a self-signed CA cert + write its SHA-256 sidecar to a temp dir, then point
        // TrustOptions at those paths. The controller must hand back the exact DER for the
        // .crt route (re-encoded from PEM in-controller via X509CertificateLoader) and the
        // verbatim PEM bytes for the .pem route — byte-for-byte. Anything else is a content
        // corruption regression.
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

        // Legacy MIME negotiation: clients that explicitly Accept application/x-x509-ca-cert
        // (Safari / iOS profile flow) get the legacy type back; default is the modern
        // application/pkix-cert from the unsuffixed request above.
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
        // HEAD is mandatory wherever GET is supported (RFC 9110 §9.3.2) and is the request
        // CDNs / browsers / curl -I issue when revalidating a cached artefact - including
        // the bootstrapper integration test's curl-based ETag check. Without [HttpHead]
        // alongside [HttpGet] on each action, the routing layer rejects HEAD as
        // "no matching endpoint", which then falls through to the default authorize policy
        // and returns 401 with a WWW-Authenticate: Bearer challenge - the exact regression
        // we shipped once. This test pins HEAD against the same matrix the GET tests cover
        // and asserts the cache contract (ETag, Cache-Control) is identical so cache
        // revalidation behaves correctly.
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
        // The trust-distribution bootstrap requires plain HTTP at /.well-known/* because
        // end-user devices fetching the root CA have no trust path to HTTPS yet. A
        // UseHttpsRedirection 308 to https:// would either (a) fail TLS handshake because
        // the device doesn't trust the leaf, or (b) redirect to the container-internal
        // HTTPS port which isn't reachable from external clients - either way the bootstrap
        // breaks. Program.cs bypasses HttpsRedirection for /.well-known/* exclusively;
        // this test pins that bypass against the matrix of routes the controller serves
        // and asserts the rest of the pipeline still redirects (so the bypass stays narrowly
        // scoped to the trust-bootstrap surface and nothing else).
        //
        // HTTPS_PORT activates HttpsRedirectionMiddleware in-test - without it the
        // middleware logs a warning and lets every request through, so a regression
        // would slip past the assertion below. The exact port doesn't matter because
        // we're not following redirects.
        await using var h = await NewTrustCertHarnessAsync(
            noRedirect: true, extraConfig: ("HTTPS_PORT", "443"));

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

        // Negative control: every other path must still redirect, proving the middleware
        // is wired and the bypass is narrowly scoped to /.well-known/* only.
        var control = await h.Client.GetAsync("/api/i-do-not-exist");
        await Assert.That((int)control.StatusCode is >= 300 and < 400).IsTrue()
            .Because($"non-.well-known paths must still go through HttpsRedirection (got {(int)control.StatusCode})");
    }

    /// <summary>
    /// Stands up a full "trust controller under test" harness: a temp dir holding a freshly-minted
    /// self-signed CA + its SHA-256 sidecar, an <see cref="InterfoldWebApplicationFactory"/> with
    /// <c>OCTOCON_TRUST_ROOT_CA_PATH</c> / <c>OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH</c> wired to
    /// those files, and an <see cref="HttpClient"/> bound to that factory. All four resources are
    /// owned by the returned <see cref="TrustCertHarness"/> and released deterministically on
    /// <see cref="TrustCertHarness.DisposeAsync"/> — call sites use
    /// <c>await using var h = await NewTrustCertHarnessAsync(...);</c> and drop the try/finally
    /// pair entirely.
    /// </summary>
    /// <param name="noRedirect">
    /// When <see langword="true"/>, the client is built via <see cref="TestClient.NoRedirect(InterfoldWebApplicationFactory)"/>
    /// so <c>3xx</c> responses surface as-is (needed by <see cref="WellKnownRoutesBypassHttpsRedirect"/>
    /// where the assertion is "must not redirect").
    /// </param>
    /// <param name="extraConfig">
    /// Optional single extra env-var key/value applied after the two <c>OCTOCON_TRUST_ROOT_CA_*</c>
    /// keys — currently only used to set <c>HTTPS_PORT=443</c> in the no-redirect test so
    /// <c>HttpsRedirectionMiddleware</c> activates. Keep this narrow; a wider need means the
    /// harness has outgrown its shape and should sprout a proper builder.
    /// </param>
    private static async Task<TrustCertHarness> NewTrustCertHarnessAsync(
        bool noRedirect = false,
        (string Key, string Value)? extraConfig = null)
    {
        var (certPath, fingerprintPath, expectedDer, expectedFingerprint, tmpDir) = MintTempCertFiles();

        var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory)
            .WithConfiguration("OCTOCON_TRUST_ROOT_CA_PATH", certPath)
            .WithConfiguration("OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH", fingerprintPath);
        if (extraConfig is { } extra)
        {
            factory.WithConfiguration(extra.Key, extra.Value);
        }

        var client = noRedirect ? TestClient.NoRedirect(factory) : factory.CreateClient();

        // Force at least one round-trip so the factory's TestServer is booted before the caller
        // starts asserting — matches the eager initialisation the pre-harness try-blocks got by
        // calling CreateClient() then GetAsync() in sequence.
        await Task.Yield();

        return new TrustCertHarness(client, factory, tmpDir,
            certPath, expectedDer, expectedFingerprint);
    }

    /// <summary>
    /// Owns the full trust-controller test surface: <see cref="HttpClient"/>, backing
    /// <see cref="InterfoldWebApplicationFactory"/>, and the temp dir holding the minted cert +
    /// fingerprint files. <see cref="DisposeAsync"/> tears down in reverse: client first (so no
    /// in-flight response outlives the server), then the factory (which stops the in-memory host),
    /// then the temp dir (best-effort — a leaked dir is harmless and the OS tmp reaper cleans it).
    /// </summary>
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

    /// <summary>
    /// Produces a temp directory holding <c>rootCA.crt</c> (PEM) and <c>rootCA.sha256.txt</c>
    /// (uppercase colon-hex) for a freshly-minted self-signed CA. Returns the paths, the
    /// expected DER bytes for byte-for-byte response assertions, the expected fingerprint,
    /// and the temp-dir path so the caller (currently only <see cref="TrustCertHarness"/>)
    /// can delete it during teardown.
    /// </summary>
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
