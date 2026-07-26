using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// End-to-end verification of the trust-distribution flow: bootstrap a full stack inside
/// DinD, then prove the same root CA is reachable over both filesystem (via the
/// bootstrapper-issued <c>certs/</c> dir) and HTTP (via the API container's
/// <c>/.well-known/interfold-root-ca.*</c> routes). This closes the loop between
/// CertificatePhase (which produces the artefacts), AppHost (which env-wires the
/// container), TrustOptions (which binds the env vars) and TrustController (which serves
/// the bytes) — a regression in any of those four points would fail this single test.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class TrustDownloadTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task WellKnownEndpointServesBootstrapperCert()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(WellKnownEndpointServesBootstrapperCert), TestConfigPaths.DefaultConfig);

        // On-disk artefacts the bootstrapper just produced — our golden truth for the three
        // endpoint responses.
        var rootCrtOnDisk = await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/rootCA.crt");
        var fingerprintOnDisk = Encoding.UTF8.GetString(
            await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/rootCA.sha256.txt")).Trim();

        var apiPort = scratch.Ports.ApiHttp;
        var baseUrl = $"http://localhost:{apiPort}/.well-known/interfold-root-ca";

        // --- .pem route: bytes-on-the-wire equal bytes-on-disk ---
        var pemResp = await dinD.ExecAsync(["curl", "-fsSL", $"{baseUrl}.pem"]);
        await Assert.That(pemResp.ExitCode).IsEqualTo(0)
            .Because($"curl {baseUrl}.pem failed: stdout='{pemResp.Stdout}' stderr='{pemResp.Stderr}'");
        var pemBytesFetched = Encoding.UTF8.GetBytes(pemResp.Stdout);
        await Assert.That(pemBytesFetched).IsEquivalentTo(rootCrtOnDisk)
            .Because("the .pem route must stream rootCA.crt verbatim");

        // --- .sha256 route: text equals the recorded fingerprint ---
        var sha256Resp = await dinD.ExecAsync(["curl", "-fsSL", $"{baseUrl}.sha256"]);
        await Assert.That(sha256Resp.ExitCode).IsEqualTo(0)
            .Because($"curl {baseUrl}.sha256 failed: stdout='{sha256Resp.Stdout}' stderr='{sha256Resp.Stderr}'");
        await Assert.That(sha256Resp.Stdout.Trim()).IsEqualTo(fingerprintOnDisk)
            .Because("the .sha256 route must equal rootCA.sha256.txt verbatim");

        // --- .crt route: response (DER) must round-trip through the recorded fingerprint ---
        // Curl is piped through base64 on the DinD side because the body is binary and
        // ExecAsync.Stdout is a string — UTF-8 decoding would mangle the DER bytes otherwise.
        var derResp = await dinD.ExecAsync(["sh", "-c", $"curl -fsSL {baseUrl}.crt | base64 -w0"]);
        await Assert.That(derResp.ExitCode).IsEqualTo(0)
            .Because($"curl {baseUrl}.crt | base64 failed: stdout='{derResp.Stdout}' stderr='{derResp.Stderr}'");
        var derFetched = Convert.FromBase64String(derResp.Stdout.Trim());

        using var diskCert = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(rootCrtOnDisk));
        await Assert.That(derFetched).IsEquivalentTo(diskCert.RawData)
            .Because("the .crt route must serve the cert's DER (X509Certificate2.RawData) byte-for-byte");

        // Trust-on-first-use checkpoint: a client that downloads the cert AND the fingerprint
        // and runs sha256(.crt) must match the fingerprint, which is the entire point of the
        // OOB-verification recipe documented in the trust-distribution section.
        var liveFingerprint = string.Join(":", SHA256.HashData(derFetched).Select(b => b.ToString("X2")));
        await Assert.That(liveFingerprint).IsEqualTo(fingerprintOnDisk)
            .Because("sha256(downloaded .crt) must equal the published fingerprint");

        // --- ETag invariant: every route returns the fingerprint-derived ETag ---
        var headersResp = await dinD.ExecAsync(["sh", "-c", $"curl -sSI {baseUrl}.crt | tr -d '\\r'"]);
        await Assert.That(headersResp.ExitCode).IsEqualTo(0).Because(headersResp.Stderr);
        await Assert.That(headersResp.Stdout).Contains($"ETag: \"{fingerprintOnDisk}\"")
            .Because("the .crt route must publish the fingerprint as its ETag for cache invalidation on --rotate-certs");
        await Assert.That(headersResp.Stdout).Contains("Cache-Control: public, max-age=60, must-revalidate")
            .Because("the .crt route must publish a 60s cacheable Cache-Control directive");
    }
}



