namespace Interfold.Contracts;

/// <summary>
/// The custom HTTP header names the API reads and writes. Spellings are frozen — the
/// Kotlin client and the socket endpoint relay depend on them.
/// </summary>
public static class InterfoldHeaders
{
    public const string OperationId = "X-Interfold-OperationId";
    public const string IdempotencyKey = "X-Interfold-Idempotency-Key";
    public const string CommandId = "X-Interfold-Command-Id";
    public const string Contract = "X-Interfold-Contract";
    public const string RequestId = "X-Interfold-Request-Id";

    /// <summary>
    /// Generic proxy-supplied correlation header ACCEPTED on inbound requests. Deliberately
    /// asymmetric with <see cref="RequestId"/>: reverse proxies and load balancers stamp the
    /// generic spelling, while responses echo the branded one.
    /// </summary>
    public const string InboundRequestId = "X-Request-Id";
    public const string Principal = "X-Interfold-Principal";
}

/// <summary>
/// Cookie names used by the OAuth login/link flows. Frozen — in-flight OAuth round-trips
/// span deployments, so a rename would strand users mid-flow.
/// </summary>
public static class InterfoldCookieNames
{
    public const string AuthRedirectUri = "octocon_auth_redirect_uri";
    public const string LinkToken = "octocon_link_token";
    public const string LinkRedirectUri = "octocon_link_redirect_uri";
}

/// <summary>JWT claim names the auth pipeline reads from issued tokens.</summary>
public static class JwtClaimNames
{
    public const string Sub = "sub";
    public const string Jti = "jti";
}

/// <summary>Named <c>HttpClient</c> registrations.</summary>
public static class HttpClientNames
{
    public const string SimplyPlural = "SimplyPlural";
}
