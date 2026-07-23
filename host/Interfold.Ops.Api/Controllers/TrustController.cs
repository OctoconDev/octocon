using System.Security.Cryptography.X509Certificates;
using Interfold.Contracts.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Controllers;

/// <summary>
/// Serves the bootstrapper-issued root CA at the IANA-registered <c>/.well-known/</c> prefix
/// so unauthenticated end-user devices can fetch and trust it before they ever speak TLS to
/// the API. Three artefact shapes are exposed:
/// <list type="bullet">
///   <item><c>interfold-root-ca.crt</c> — DER (binary), <c>application/pkix-cert</c> by default
///         and <c>application/x-x509-ca-cert</c> when the client opts in via <c>Accept</c>
///         (the legacy MIME that triggers Safari/iOS profile flows).</item>
///   <item><c>interfold-root-ca.pem</c> — PEM (base64-armoured), <c>application/x-pem-file</c>.</item>
///   <item><c>interfold-root-ca.sha256</c> — the SHA-256 fingerprint of the root cert in
///         uppercase colon-hex (matches <c>openssl x509 -fingerprint -sha256</c>).</item>
/// </list>
///
/// The controller serves a hardcoded allowlist of three paths derived from
/// <see cref="TrustOptions"/>; no user input ever joins a file path, so a path-traversal
/// regression here cannot read <c>rootCA.key</c> (which also sits at mode 0600 in the same
/// directory as defence-in-depth). When the bootstrapper hasn't supplied either path (dev
/// mode, missing bind mount), every route returns 404.
///
/// <para>
/// Cache strategy: <c>Cache-Control: public, max-age=60, must-revalidate</c> plus an
/// <c>ETag</c> derived from the SHA-256 fingerprint file. Rotating the root CA via
/// <c>bootstrap --rotate-certs</c> rewrites the sidecar fingerprint, which changes the
/// ETag, which causes downstream caches to refresh on the next conditional request.
/// </para>
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(".well-known")]
public sealed class TrustController : ControllerBase
{
    private readonly TrustOptions _options;

    public TrustController(IOptions<TrustOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Returns the root CA in DER (binary) form. PEM-on-disk → DER conversion happens
    /// in-controller via <see cref="X509CertificateLoader"/> so the on-disk format never
    /// becomes part of the wire contract; the bootstrapper is free to write either.
    /// Negotiates the legacy <c>application/x-x509-ca-cert</c> MIME when requested via
    /// Accept for Safari/iOS-friendly profile flows; otherwise emits the modern RFC 2585
    /// <c>application/pkix-cert</c>.
    /// </summary>
    [HttpGet("interfold-root-ca.crt")]
    [HttpHead("interfold-root-ca.crt")]
    public async Task<IActionResult> GetRootCaDer(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.RootCaPath) || !System.IO.File.Exists(_options.RootCaPath))
        {
            return NotFound();
        }

        using var cert = X509CertificateLoader.LoadCertificateFromFile(_options.RootCaPath);
        var der = cert.RawData;

        await ApplyCacheHeadersAsync(ct).ConfigureAwait(false);

        var contentType = AcceptsLegacyCaCertMime() ? "application/x-x509-ca-cert" : "application/pkix-cert";
        return File(der, contentType, "interfold-root-ca.crt");
    }

    /// <summary>
    /// Returns the root CA in PEM form. The on-disk file is already PEM (CertificatePhase
    /// writes via <c>X509Certificate2.ExportCertificatePem()</c>) so we stream the bytes
    /// verbatim without re-encoding.
    /// </summary>
    [HttpGet("interfold-root-ca.pem")]
    [HttpHead("interfold-root-ca.pem")]
    public async Task<IActionResult> GetRootCaPem(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.RootCaPath) || !System.IO.File.Exists(_options.RootCaPath))
        {
            return NotFound();
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(_options.RootCaPath, ct).ConfigureAwait(false);

        await ApplyCacheHeadersAsync(ct).ConfigureAwait(false);
        return File(bytes, "application/x-pem-file", "interfold-root-ca.pem");
    }

    /// <summary>
    /// Returns the SHA-256 fingerprint of the root CA as a single line of uppercase colon-hex
    /// text. The fingerprint is what operators distribute out-of-band (Slack, email,
    /// keybase) so end users can pin it against the cert they fetched from this same
    /// origin — closing the trust-on-first-fetch gap.
    /// </summary>
    [HttpGet("interfold-root-ca.sha256")]
    [HttpHead("interfold-root-ca.sha256")]
    public async Task<IActionResult> GetRootCaFingerprint(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.RootCaFingerprintPath) ||
            !System.IO.File.Exists(_options.RootCaFingerprintPath))
        {
            return NotFound();
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(_options.RootCaFingerprintPath, ct).ConfigureAwait(false);

        await ApplyCacheHeadersAsync(ct).ConfigureAwait(false);
        return File(bytes, "text/plain; charset=utf-8", "interfold-root-ca.sha256");
    }

    /// <summary>
    /// Stamps <c>Cache-Control</c> and a fingerprint-based <c>ETag</c> on the current
    /// response. The ETag is the only mechanism that propagates a <c>--rotate-certs</c>
    /// signal through downstream caches; without it a CDN or browser holding a stale copy
    /// would keep serving the old cert until <c>max-age</c> expired.
    /// </summary>
    private async Task ApplyCacheHeadersAsync(CancellationToken ct)
    {
        Response.Headers["Cache-Control"] = "public, max-age=60, must-revalidate";

        if (string.IsNullOrWhiteSpace(_options.RootCaFingerprintPath) ||
            !System.IO.File.Exists(_options.RootCaFingerprintPath))
        {
            return;
        }

        var fp = (await System.IO.File.ReadAllTextAsync(_options.RootCaFingerprintPath, ct).ConfigureAwait(false)).Trim();
        if (fp.Length > 0)
        {
            Response.Headers.ETag = $"\"{fp}\"";
        }
    }

    /// <summary>
    /// True when the client explicitly opts in to the legacy
    /// <c>application/x-x509-ca-cert</c> MIME via its <c>Accept</c> header. Modern Safari
    /// / iOS profile install flows still trigger on this type even though it's been
    /// superseded by <c>application/pkix-cert</c> (RFC 2585).
    /// </summary>
    private bool AcceptsLegacyCaCertMime()
    {
        foreach (var accept in Request.Headers.Accept)
        {
            if (string.IsNullOrEmpty(accept))
            {
                continue;
            }

            if (accept.Contains("application/x-x509-ca-cert", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
