using System.Net.WebSockets;
using System.IdentityModel.Tokens.Jwt;
using Interfold.Shared.Domain.Abstractions;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions.Repository;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Socket;

public static class WebSocketHandler
{
public static async Task HandleUserSocketAsync(HttpContext context)
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("WebSocketHandler");
    
    logger.LogInformation(
        "WebSocket request received. Method: {Method}, Path: {Path}, IsWebSocketRequest: {IsWSRequest}",
        context.Request.Method,
        context.Request.Path,
        context.WebSockets.IsWebSocketRequest);

    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.LogWarning("Request is not a WebSocket upgrade request");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(
            new ErrorResponse("WebSocket upgrade required.", ErrorCodes.WebSocketUpgradeRequired));
        return;
    }

    // Wrap at the boundary so accidental $"{token}" interpolations go through SocketToken.ToString()
    // and get redacted. .Value is unwrapped only where the raw JWT is unavoidable.
    SocketToken token = new(context.Request.Query[SocketQueryKeys.Token].ToString());
    logger.LogInformation("Token from query string length: {TokenLength}", token.Value.Length);

    if (string.IsNullOrWhiteSpace(token.Value))
    {
        logger.LogWarning("Missing or empty token in query string");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(
            new ErrorResponse("Missing socket token.", ErrorCodes.MissingSocketToken));
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var batchedInitThresholdBytes = context.RequestServices
        .GetRequiredService<IOptionsMonitor<SocketConfiguration>>()
        .CurrentValue.BatchBytesThreshold ?? 1_048_576;
    var buffer = new byte[1024 * 16];
    var joinedTopics = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
    SystemId? joinedSystemId = null;
    var topicReplyAsArrayFrame = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    var topicJoinReference = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
    using var sendGate = new SemaphoreSlim(1, 1);
    using var pushCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var eventBus = context.RequestServices.GetRequiredService<IClusterEventBus>();
    var rateLimiter = context.RequestServices.GetRequiredService<SocketJoinRateLimiter>();
    var frontingRepository = context.RequestServices.GetRequiredService<IFrontingRepository>();
    var alterRepository = context.RequestServices.GetRequiredService<IAlterRepository>();
    var tagRepository = context.RequestServices.GetRequiredService<ITagRepository>();
    var settingsFieldRepository = context.RequestServices.GetRequiredService<ISettingsFieldRepository>();
    var accountRepository = context.RequestServices.GetRequiredService<IAccountRepository>();
    var friendshipRepository = context.RequestServices.GetRequiredService<IFriendshipRepository>();
    var pollRepository = context.RequestServices.GetRequiredService<IPollRepository>();
    var journalRepository = context.RequestServices.GetRequiredService<IJournalRepository>();
    var encryptionStateRepository = context.RequestServices.GetRequiredService<IEncryptionStateRepository>();
    var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
    var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host}";

    // The per-socket event pump is started on the first successful phx_join (see below).
    // Deferring registration keeps each socket invisible to the bus until it actually
    // identifies a target system; without it, every just-accepted socket added ~38 broadcast
    // writers to the bus and the publisher had to fan every event out to all of them.
    Task? socketPushTask = null;
    // 0 = pump not started, 1 = started. Mutated only via Interlocked.CompareExchange so a
    // burst of phx_join frames that arrives back-to-back (e.g. retried by the client during
    // a flaky connection) can never race past the guard and spin up a second pump.
    var pumpStarted = 0;

    while (socket.State == WebSocketState.Open)
    {
        var incomingText = await ReceiveSocketTextAsync(socket, buffer, context.RequestAborted);
        if (incomingText is null)
        {
            break;
        }

        if (!PhoenixInboundFrame.TryParse(incomingText, out var frame))
        {
            // Unrecognised frame; close with a protocol-error code rather than
            // echoing raw JSON (which is itself not a valid Phoenix frame).
            await socket.CloseAsync(
                WebSocketCloseStatus.InvalidPayloadData,
                "invalid_phoenix_frame",
                context.RequestAborted);
            break;
        }

        var (eventName, topic, payload, reference, joinReference, replyAsArrayFrame) = frame;

        if (string.Equals(eventName, PhoenixEventNames.Heartbeat, StringComparison.OrdinalIgnoreCase))
        {
            await SendPhoenixReplyAsync(
                socket,
                topic,
                reference,
                joinReference,
                status: PhoenixReplyStatus.Ok,
                response: new EmptyPayload(),
                replyAsArrayFrame,
                context.RequestAborted,
                sendGate);
            continue;
        }

        if (string.Equals(eventName, PhoenixEventNames.Join, StringComparison.OrdinalIgnoreCase))
        {
            // Deserialize the typed join payload. Per-property tolerance for the ONE
            // field that historically saw stray wire spellings — the platform enum —
            // is enforced by TolerantWireEnumJsonConverter on PhxJoinPayload.Platform,
            // so an unknown platform value round-trips to null WITHOUT throwing a
            // JsonException that would take the sibling token / protocolVersion down
            // with it. The outer try/catch stays as a belt-and-braces guard for
            // wholly-malformed payloads (e.g. `payload: 42`) where "keep defaults"
            // is a safer floor than crashing the socket loop, but a valid join with
            // an unknown platform must never hit this catch — that path would zero
            // out the token and turn the reply into a bogus Unauthorized (regression
            // pinned by Api_UserSocketEndpoint_AllowsWebSocketUpgrade).
            var joinPayload = new PhxJoinPayload();
            if (payload?.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    joinPayload = payload.Value.Deserialize<PhxJoinPayload>(SocketJson.Options) ?? new PhxJoinPayload();
                }
                catch (JsonException)
                {
                    // keep defaults
                }
            }

            var payloadToken = joinPayload.Token;
            var isReconnect = joinPayload.IsReconnect ?? false;
            var forceBatch = joinPayload.ForceBatch ?? false;
            var platform = joinPayload.Platform;
            var protocolVersion = new Version(1, 0, 0);
            var protocolSupported = joinPayload.ProtocolVersion is null
                || TryParseLooseVersion(joinPayload.ProtocolVersion, out protocolVersion);

            var isSystemTopic = SystemTopic.TryParse(topic, out var requestedTopic);
            // SystemId? mirrors the joinedSystemId idiom above — null means "no system
            // topic on this join" and gates the downstream sub-vs-topic comparison.
            SystemId? requestedSystemId = isSystemTopic ? requestedTopic.Id : null;
            // scopedSub is the ScopedSystemId? parsed from the JWT sub inside the helper.
            // Feeding it into SocketPushContext.JoinedScopedSystemId lets the event-pump
            // subscribe with the scoped composite (matching every
            // ITargetedClusterEvent.TargetSystemId), so the bus PublishAsync filter can
            // compare scoped-to-scoped directly.
            var (tokenAuthorized, tokenAuthFailureReason, scopedSub) = await IsSocketJoinTokenAuthorizedAsync(
                context,
                token,
                requestedSystemId,
                context.RequestAborted);

            if (!protocolSupported)
            {
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: PhoenixReplyStatus.Error,
                    response: new SocketReasonResponse(ErrorCodes.SocketReasons.UnsupportedProtocolVersion),
                    replyAsArrayFrame,
                        context.RequestAborted,
                        sendGate);
            }
            else if (isSystemTopic
                && payloadToken == token
                && tokenAuthorized)
            {
                if (!rateLimiter.Allow(requestedTopic.Id))
                {
                    await SendPhoenixReplyAsync(
                        socket,
                        topic,
                        reference,
                        joinReference,
                        status: PhoenixReplyStatus.Error,
                        response: new SocketReasonResponse(ErrorCodes.SocketReasons.RateLimited),
                        replyAsArrayFrame,
                        context.RequestAborted,
                        sendGate);
                    continue;
                }

                joinedTopics[topic] = 0;
                joinedSystemId = requestedTopic.Id;
                topicReplyAsArrayFrame[topic] = replyAsArrayFrame;
                topicJoinReference[topic] = joinReference;

                // Start the per-socket event pump now that we know which system this socket is bound to.
                // The bus filter only delivers events whose TargetSystemId matches
                // JoinedScopedSystemId (scoped-to-scoped record-struct equality), so the
                // pump's ~38 subscriptions only see traffic for this user.
                //
                // Interlocked.CompareExchange flips pumpStarted from 0 to 1 atomically and returns the
                // previous value; only the thread that observed 0 actually constructs the push context
                // and starts the pump. A subsequent phx_join on the same socket simply observes 1 and
                // becomes a no-op rather than spinning a second pump / second SocketPushContext.
                if (Interlocked.CompareExchange(ref pumpStarted, 1, 0) == 0)
                {
                    // scopedSub (from IsSocketJoinTokenAuthorizedAsync) threads through
                    // SocketPushContext into every SocketEventPumpRunner subscription, so
                    // the bus filter compares scoped-to-scoped without a runtime
                    // StripRegionPrefix normalisation on either side.
                    var socketPushContext = new SocketPushContext(
                        socket,
                        scopedSub,
                        joinedTopics,
                        topicJoinReference,
                        topicReplyAsArrayFrame,
                        sendGate,
                        pushCts.Token,
                        requestOrigin: requestOrigin,
                        logger: logger,
                        timeProvider: timeProvider);

                    socketPushTask = SocketEventPumpRunner.RunAllAsync(
                        eventBus,
                        socketPushContext,
                        frontingRepository,
                        alterRepository,
                        tagRepository,
                        settingsFieldRepository,
                        accountRepository,
                        friendshipRepository,
                        pollRepository,
                        journalRepository,
                        encryptionStateRepository);
                }

                var initPayload = await WebSocketInitialization.BuildJoinInitPayloadAsync(context, joinedSystemId.Value, context.RequestAborted);
                var useBatchedInit = false;

                if (!isReconnect)
                {
                    var estimatedEncodedBytes = (int)(Encoding.UTF8.GetByteCount(WebSocketEvents.SerializeSocketJson(initPayload)) * 1.1);
                    useBatchedInit = forceBatch
                        || (platform == ClientPlatform.Ios
                            && estimatedEncodedBytes > batchedInitThresholdBytes
                            && protocolVersion >= new Version(2, 0, 0));
                }

                // Deliberately `object`, not ISocketPayload: System.Text.Json serializes
                // interface-declared values by the interface's (empty) member set, while
                // `object` triggers runtime-type serialization — which is what puts the
                // payload's real properties on the wire.
                object joinResponse;
                if (isReconnect)
                {
                    joinResponse = new SocketJoinReconnectPayload(initPayload.System);
                }
                else if (useBatchedInit)
                {
                    joinResponse = new SocketJoinBatchedPayload(
                        Batched: true,
                        System: initPayload.System,
                        Alters: null,
                        Fronts: null,
                        Tags: null);
                }
                else
                {
                    joinResponse = initPayload;
                }

                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: PhoenixReplyStatus.Ok,
                    response: joinResponse,
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);

                if (useBatchedInit)
                {
                    await WebSocketInitialization.SendBatchedInitAsync(
                        socket,
                        topic,
                        topicJoinReference[topic],
                        topicReplyAsArrayFrame[topic],
                        initPayload,
                        context.RequestAborted,
                        sendGate);
                }
            }
            else
            {
                var unauthorizedReason = tokenAuthFailureReason ?? ErrorCodes.SocketReasons.Unauthorized;
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: PhoenixReplyStatus.Error,
                    response: new SocketReasonResponse(unauthorizedReason),
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, unauthorizedReason.Value, context.RequestAborted);
            }

            continue;
        }

        if (string.Equals(eventName, PhoenixEventNames.Endpoint, StringComparison.OrdinalIgnoreCase))
        {
            if (!joinedTopics.ContainsKey(topic))
            {
                await SendPhoenixReplyAsync(
                    socket,
                    topic,
                    reference,
                    joinReference,
                    status: PhoenixReplyStatus.Error,
                    response: new SocketReasonResponse(ErrorCodes.SocketReasons.NotJoined),
                    replyAsArrayFrame,
                    context.RequestAborted,
                    sendGate);
                continue;
            }

            var endpointResult = await HandleEndpointProxyAsync(context, payload, token, joinedSystemId);

            await SendPhoenixReplyAsync(
                socket,
                topic,
                reference,
                joinReference,
                status: PhoenixReplyStatus.Ok,
                response: endpointResult,
                replyAsArrayFrame,
                context.RequestAborted,
                sendGate);
            continue;
        }

        await SendPhoenixReplyAsync(
            socket,
            topic,
            reference,
            joinReference,
            status: PhoenixReplyStatus.Error,
            response: new SocketReasonResponse(ErrorCodes.SocketReasons.EventNotImplemented),
            replyAsArrayFrame,
            context.RequestAborted,
            sendGate);
    }

    await pushCts.CancelAsync();
    if (socketPushTask is not null)
    {
        try
        {
            await socketPushTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on socket shutdown.
        }
    }

    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
    {
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "socket closed",
            CancellationToken.None);
    }
}

static async Task<SocketEndpointProxyResponse> HandleEndpointProxyAsync(
    HttpContext websocketContext,
    JsonElement? payload,
    SocketToken socketToken,
    SystemId? joinedSystemId)
{
    if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
    {
        return new SocketEndpointProxyResponse(
            System.Net.HttpStatusCode.BadRequest,
            ToJsonString(new ErrorResponse(
                "Invalid endpoint payload.",
                ErrorCodes.SocketEndpointPayloadInvalid,
                System.Net.HttpStatusCode.BadRequest)));
    }

    SocketEndpointProxyRequest? proxyRequest;
    try
    {
        proxyRequest = payload.Value.Deserialize<SocketEndpointProxyRequest>(SocketJson.Options);
    }
    catch (JsonException)
    {
        proxyRequest = null;
    }

    var method = proxyRequest?.Method ?? string.Empty;
    var path = proxyRequest?.Path ?? string.Empty;

    if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
    {
        return new SocketEndpointProxyResponse(
            System.Net.HttpStatusCode.BadRequest,
            ToJsonString(new ErrorResponse(
                "Endpoint payload must include method and path.",
                ErrorCodes.SocketEndpointMethodPathRequired,
                System.Net.HttpStatusCode.BadRequest)));
    }

    if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
    {
        return new SocketEndpointProxyResponse(
            System.Net.HttpStatusCode.Forbidden,
            ToJsonString(new ErrorResponse(
                "Socket endpoint relay is restricted to /api paths.",
                ErrorCodes.SocketEndpointPathForbidden,
                System.Net.HttpStatusCode.Forbidden)));
    }

    // Self-call the API's own Kestrel listener rather than dialing back through
    // `Request.Scheme`/`Request.Host`. In the published docker compose stack the
    // request arrives via the operator-facing hostname + host-mapped port (e.g.
    // `https://api.example.com:5001`), but neither is reachable from inside the
    // container: the hostname is rarely in the container's DNS namespace, and the
    // host-side port is the OUTSIDE of the compose port mapping (the container
    // itself listens on `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_HTTPS_PORTS`,
    // default 5100/5101, configurable via `Ports:api-container-http(s)`).
    // Resolving via IServerAddressesFeature picks up whatever Kestrel actually
    // bound to in this process, regardless of the deployment topology in front
    // of it. We prefer the HTTPS binding so the call doesn't trip
    // HttpsRedirection (which would 308 us at the HTTPS listener anyway), and
    // dial it through the named `LoopbackHttpClient` whose permissive TLS
    // validator accepts the local leaf cert despite the SAN/chain mismatches —
    // see `LoopbackHttpClient` for the safety argument.
    var server = websocketContext.RequestServices.GetRequiredService<IServer>();
    var baseUri = ResolveLoopbackBaseUri(server.Features.Get<IServerAddressesFeature>()?.Addresses);
    var targetUri = $"{baseUri}{path}";

    if (!Uri.TryCreate(targetUri, UriKind.Absolute, out var parsedTargetUri)
        || (parsedTargetUri.Scheme is "https" && !LoopbackHttpClient.IsLoopbackHost(parsedTargetUri)))
    {
        // Defence-in-depth: ResolveLoopbackBaseUri is unit-tested to always emit a loopback
        // shape (or the TestServer fallback `http://localhost`), but the named loopback
        // HttpClient skips TLS validation, so a future regression that let a non-loopback
        // host through here would silently weaken every self-call. Fail fast instead.
        return new SocketEndpointProxyResponse(
            System.Net.HttpStatusCode.InternalServerError,
            ToJsonString(new ErrorResponse(
                "Socket endpoint relay resolved a non-loopback target.",
                ErrorCodes.SocketEndpointProxyMisrouted,
                System.Net.HttpStatusCode.InternalServerError)));
    }

    using var request = new HttpRequestMessage(new HttpMethod(method), parsedTargetUri);
    // Forward the outer Host so the inner controller's `Request.Host` is the operator-facing
    // origin, not the loopback dial target — anything reading `Request.Host` for URL
    // qualification (`QualifyAvatar`, OAuth callbacks) would otherwise emit unreachable URLs.
    request.Headers.Host = websocketContext.Request.Host.Value;
    // Wire-format unwrap: this is the single site in the file (paired with the JWT
    // framework calls in IsSocketJoinTokenAuthorizedAsync) where SocketToken.Value is
    // exposed. Interpolating the wrapper itself would emit the redacted `abcd…` form and
    // authentication would fail — the actual JWT must go on the wire verbatim.
    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {socketToken.Value}");
    request.Headers.TryAddWithoutValidation("Accept", "application/json");

    if (joinedSystemId is { } principal && !string.IsNullOrWhiteSpace(principal.Value))
    {
        request.Headers.TryAddWithoutValidation(InterfoldHeaders.Principal, principal.Value);
    }

    // Body is deserialized straight from the payload's JSON string (Phoenix carries
    // the inner request body as a JSON-string field so we forward it byte-identical
    // without re-serializing).
    if (!string.IsNullOrWhiteSpace(proxyRequest?.Body)
        && method is not "GET" and not "HEAD")
    {
        request.Content = new StringContent(proxyRequest.Body, Encoding.UTF8, "application/json");
    }

    var httpClientFactory = websocketContext.RequestServices.GetRequiredService<IHttpClientFactory>();
    using var httpClient = httpClientFactory.CreateClient(LoopbackHttpClient.Name);

    var response = await httpClient.SendAsync(request, websocketContext.RequestAborted);
    try
    {
        var responseBody = await response.Content.ReadAsStringAsync(websocketContext.RequestAborted);
        return new SocketEndpointProxyResponse(response.StatusCode, responseBody);
    }
    finally
    {
        response.Dispose();
    }
}

static string ToJsonString<T>(T value)
    => JsonSerializer.Serialize(value, SocketJson.Options);

/// <summary>Loopback-safe base URL for the socket relay's self-call. Prefers https to skip
/// UseHttpsRedirection; wildcard hosts (0.0.0.0/[::]/+/*) → 127.0.0.1; empty addresses →
/// http://localhost so TestServer stays functional. HTTPS TLS validation is handled by
/// <see cref="LoopbackHttpClient"/>'s permissive validator (safe: loopback-only target).</summary>
internal static string ResolveLoopbackBaseUri(ICollection<string>? addresses)
{
    const string testServerFallback = "http://localhost";
    if (addresses is null || addresses.Count == 0) return testServerFallback;

    var preferred = addresses
        .OrderBy(static addr => addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .FirstOrDefault();
    if (preferred is null) return testServerFallback;

    return preferred.TrimEnd('/')
        .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
        .Replace("://[::]",    "://127.0.0.1", StringComparison.Ordinal)
        .Replace("://+",       "://127.0.0.1", StringComparison.Ordinal)
        .Replace("://*",       "://127.0.0.1", StringComparison.Ordinal);
}

/// <summary>Region-prefix-tolerant equality between the scoped JWT sub and a possibly-raw
/// socket topic id. Third of three sites (after middleware and <c>InProcessEventBus</c>)
/// that must agree on "same principal"; scoped-sub type argument forces callers through the
/// same TryParseScoped gate the middleware uses.</summary>
internal static bool IsTokenSubjectAuthorizedForTopic(
    ScopedSystemId? tokenSubject,
    SystemId? requestedSystemId)
{
    if (tokenSubject is null
        || requestedSystemId is null
        || string.IsNullOrWhiteSpace(requestedSystemId.Value.Value))
    {
        return false;
    }

    return string.Equals(
        tokenSubject.Value.RawId,
        ScopedSystemId.StripRegionPrefix(requestedSystemId.Value.Value),
        StringComparison.Ordinal);
}

// Returns the parsed ScopedSystemId so HandleAsync can populate SocketPushContext without
// re-parsing the JWT.
static async Task<(bool IsAuthorized, ErrorCode? FailureReason, ScopedSystemId? TokenSubject)> IsSocketJoinTokenAuthorizedAsync(
    HttpContext context,
    SocketToken token,
    SystemId? requestedSystemId,
    CancellationToken cancellationToken)
{
    var authConfig = context.RequestServices
        .GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>().CurrentValue;

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("WebSocketTokenAuth");

    if (string.IsNullOrWhiteSpace(token.Value))
    {
        logger.LogWarning("Token is empty or whitespace");
        return (false, ErrorCodes.SocketReasons.MissingSocketToken, null);
    }

    logger.LogInformation("Validating token. RequestedSystemId: {SystemId}", requestedSystemId);

    // .Value unwraps are the JWT framework survival points; everything else uses the
    // redacted-by-default wrapper.
    var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
    if (!handler.CanReadToken(token.Value))
    {
        logger.LogWarning("Handler cannot read token");
        return (false, ErrorCodes.SocketReasons.InvalidSocketToken, null);
    }

    logger.LogInformation("Token is readable. Verification key count: {KeyCount}", 
        authConfig.JwtEs256VerificationKeyPems?.Length ?? 0);

    var parameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidAudience = authConfig.JwtAudience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
        ValidateIssuerSigningKey = false,
        RequireSignedTokens = true,
        // Framework hands the raw string via this callback; forward to the ES256 verifier.
        SignatureValidator = (frameworkRawToken, validationParameters) =>
            ValidateJwtTokenSignatureForSocket(frameworkRawToken, validationParameters, authConfig),
        NameClaimType = JwtClaimNames.Sub
    };

    try
    {
        logger.LogInformation("Starting token validation");
        var principal = handler.ValidateToken(token.Value, parameters, out _);
        var tokenSub = principal.FindFirstValue(JwtClaimNames.Sub);

        logger.LogInformation("Token validated. TokenSystemId: {TokenSub}, RequestedSystemId: {RequestedSub}",
            tokenSub, requestedSystemId);

        // Same parse rejection matrix as InterfoldPrincipalMiddleware.ResolvePrincipalId
        // so an unscoped-sub token can't authorise a socket join it would fail on HTTP.
        if (!ScopedSystemId.TryParseScoped(tokenSub, out var scopedSub))
        {
            logger.LogWarning("Token subject (sub) claim is missing, unscoped, or has an unknown region prefix");
            return (false, ErrorCodes.SocketReasons.InvalidSocketTokenSubject, null);
        }

        if (!IsTokenSubjectAuthorizedForTopic(scopedSub, requestedSystemId))
        {
            logger.LogWarning("Token subject does not match requested system ID");
            return (false, ErrorCodes.SocketReasons.UnauthorizedTopic, null);
        }

            // Wrap so log sites route through Jti.ToString redaction.
            var jti = Jti.From(principal.FindFirstValue(JwtClaimNames.Jti));
            if (jti is { } typedJti)
            {
                var revocationRepository = context.RequestServices
                    .GetRequiredService<IAuthTokenRevocationRepository>();
                var isTokenValid = await revocationRepository.ValidateTokenNotRevokedAsync(typedJti, cancellationToken);
                if (!isTokenValid)
                {
                    logger.LogWarning("Token has been revoked. JTI: {Jti}", typedJti);
                    return (false, ErrorCodes.SocketReasons.TokenRevoked, null);
                }
            }

            logger.LogInformation("Token authorization successful");
            return (true, null, scopedSub);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "WebSocket token validation failed: {ExceptionMessage}", ex.Message);
        return (false, ErrorCodes.SocketReasons.InvalidSocketToken, null);
    }
}

static async Task<string?> ReceiveSocketTextAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
{
    using var messageStream = new MemoryStream();

    while (socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult received;
        try
        {
            received = await socket.ReceiveAsync(buffer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (received.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        if (received.MessageType != WebSocketMessageType.Text)
        {
            return null;
        }

        await messageStream.WriteAsync(buffer.AsMemory(0, received.Count), cancellationToken);

        if (received.EndOfMessage)
        {
            return Encoding.UTF8.GetString(messageStream.ToArray());
        }
    }

    return null;
}

static bool TryParseLooseVersion(string? value, out Version parsed)
{
    parsed = new Version(1, 0, 0);
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    var normalized = value.Trim();
    if (Version.TryParse(normalized, out parsed!))
    {
        return true;
    }

    // Phoenix clients typically use semver strings like "2.0.0";
    // allow a trailing prerelease segment by stripping from '-'.
    var dashIndex = normalized.IndexOf('-');
    if (dashIndex > 0)
    {
        var stable = normalized[..dashIndex];
        return Version.TryParse(stable, out parsed!);
    }

    return false;
}

static SecurityToken ValidateJwtTokenSignatureForSocket(
    string token,
    TokenValidationParameters validationParameters,
    AuthenticationConfiguration config)
{
    return Interfold.Api.Auth.JwtEs256Validator.ValidateSignature(token, config.JwtEs256VerificationKeyPems ?? [], useJsonWebToken: false);
}

 static async Task SendPhoenixReplyAsync<TResponse>(
     WebSocket socket,
     string topic,
     string? reference,
     string? joinReference,
     PhoenixReplyStatus status,
     TResponse response,
     bool replyAsArrayFrame,
     CancellationToken cancellationToken,
     SemaphoreSlim? sendGate = null)
 {
     var payload = new PhoenixReplyPayload<TResponse>(status, response);
     var bytes = replyAsArrayFrame
         ? PhxArrayFrame.CreateBytes(joinReference, reference, topic, PhoenixEventNames.Reply, payload)
         : new PhxFrame<PhoenixReplyPayload<TResponse>>
         {
             Topic = topic,
             Event = PhoenixEventNames.Reply,
             Payload = payload,
             Ref = reference,
             JoinRef = joinReference
         }.ToBytes();

    if (sendGate is not null)
    {
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }
    else
    {
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
}