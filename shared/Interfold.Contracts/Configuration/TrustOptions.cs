namespace Interfold.Contracts.Configuration;

/// <summary>
/// Trust-distribution configuration for the API's
/// <c>/.well-known/interfold-root-ca.{crt,pem,sha256}</c> endpoints (see TrustController in
/// Interfold.Api). Both paths are set by the bootstrapper-generated AppHost env block
/// (<c>OCTOCON_TRUST_ROOT_CA_PATH</c> + <c>OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH</c>) and
/// point at the read-only <c>/certs</c> bind mount. In dev (no bind mount, no env), both
/// stay null and the controller returns 404 for every route.
/// </summary>
public sealed class TrustOptions
{
    public const string SectionName = "Octocon:Trust";

    /// <summary>
    /// Absolute path inside the API process to the PEM-encoded root CA certificate served at
    /// <c>/.well-known/interfold-root-ca.crt</c> (re-encoded to DER on the wire) and
    /// <c>/.well-known/interfold-root-ca.pem</c>. <c>null</c> disables the .crt / .pem routes.
    /// Env: <c>OCTOCON_TRUST_ROOT_CA_PATH</c>.
    /// </summary>
    public string? RootCaPath { get; set; }

    /// <summary>
    /// Absolute path inside the API process to the sidecar file containing the SHA-256
    /// fingerprint of the root CA (uppercase colon-hex, matches
    /// <c>openssl x509 -fingerprint -sha256</c>). Served as text at
    /// <c>/.well-known/interfold-root-ca.sha256</c> and reused as the HTTP
    /// <c>ETag</c> on the cert routes so a <c>--rotate-certs</c> invalidates downstream
    /// caches automatically. <c>null</c> disables the .sha256 route and drops the ETag.
    /// Env: <c>OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH</c>.
    /// </summary>
    public string? RootCaFingerprintPath { get; set; }
}
