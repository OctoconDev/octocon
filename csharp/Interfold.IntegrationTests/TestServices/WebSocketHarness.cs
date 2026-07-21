using System.Net.WebSockets;
using Interfold.IntegrationTests.Endpoints;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Test-side scaffolding for the Phoenix-style websocket surface. Groups the
/// mint-token + open-socket + join-system-topic sequence that every multi-party
/// websocket test would otherwise open-code so a change to how tokens are minted,
/// how the socket URL is composed, or how <c>phx_join</c> is framed rolls forward
/// through a single call site rather than 7+ copies.
///
/// <para>
/// The single-party sister helper (<c>ConnectAndJoinAsync</c>) lands with the
/// D11 test-helper extraction pass; both helpers share this file to keep the
/// two-party / one-party symmetry legible.
/// </para>
/// </summary>
internal static class WebSocketHarness
{
    /// <summary>
    /// Provisions two authenticated sockets, opens both, and issues the initial
    /// <c>phx_join</c> against each system's <c>system:{id}</c> topic. Returns
    /// each socket alongside the raw JWT string used to authenticate it — some
    /// call sites need the token again when they construct subsequent
    /// endpoint-proxy frames from within the same test.
    ///
    /// <para>
    /// The returned <see cref="WebSocket"/>s are unowned; the caller must adopt
    /// them via <c>using var</c> at the call site so disposal is scoped to the
    /// test body rather than the harness. Standard pattern:
    /// <code>
    /// var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, aId, bId, token);
    /// using var wsA = pair.FirstWs;
    /// using var wsB = pair.SecondWs;
    /// var tokenA = pair.FirstToken;
    /// var tokenB = pair.SecondToken;
    /// </code>
    /// The <c>First</c>/<c>Second</c> naming is deliberately neutral — callers
    /// re-alias to <c>sender</c>/<c>recipient</c> (or any other directional pair)
    /// at the destructure so downstream assertions keep the semantic names.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The join step throws on a non-<c>ok</c> reply — see
    /// <c>WebSocketTests.JoinTopicAsync</c> for the reply-status guard. Tests that
    /// expect a join to fail must NOT use this helper; they should hand-roll the
    /// two-part connect + join so the failure surfaces at the assertion, not from
    /// inside the harness.
    /// </remarks>
    public static async Task<(WebSocket FirstWs, WebSocket SecondWs, string FirstToken, string SecondToken)>
        ConnectPairAndJoinAsync(
            IWebFactoryFixture fixture,
            string firstSystemId,
            string secondSystemId,
            CancellationToken cancellationToken)
    {
        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();

        var firstToken = await BaseEndpointTest.CreateRandomToken(fixture.Factory, firstSystemId);
        var secondToken = await BaseEndpointTest.CreateRandomToken(fixture.Factory, secondSystemId);

        var firstUri = new Uri(WebSocketTests.WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={firstToken}");
        var secondUri = new Uri(WebSocketTests.WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={secondToken}");

        var firstWs = await wsClientFactory.ConnectAsync(firstUri, cancellationToken);
        var secondWs = await wsClientFactory.ConnectAsync(secondUri, cancellationToken);

        // Fail-fast on the join step to keep the harness call site short — a test that
        // wanted to assert a non-ok join reply would branch into hand-rolled setup, so
        // the throw here is the right default for the "happy path pair setup" surface.
        try
        {
            await WebSocketTests.JoinTopicAsync(firstWs, $"system:{firstSystemId}", firstToken, cancellationToken);
            await WebSocketTests.JoinTopicAsync(secondWs, $"system:{secondSystemId}", secondToken, cancellationToken);
        }
        catch
        {
            // Guarantee no leaked sockets if the join throws before the caller adopts
            // ownership. Suppress the individual dispose exceptions so the original
            // join failure is what surfaces at the call site.
            try { firstWs.Dispose(); } catch { /* swallow: caller sees the join failure */ }
            try { secondWs.Dispose(); } catch { /* swallow: caller sees the join failure */ }
            throw;
        }

        return (firstWs, secondWs, firstToken, secondToken);
    }

    /// <summary>
    /// Single-party sister of <see cref="ConnectPairAndJoinAsync"/>: mints a JWT for
    /// <paramref name="systemId"/>, opens one websocket against
    /// <c>/api/socket/websocket?token=…</c>, and issues <c>phx_join</c> against
    /// <c>system:{systemId}</c>. Returns the raw <see cref="WebSocket"/> alongside the
    /// token because most call sites embed the token again in follow-up
    /// endpoint-proxy frames on the same socket.
    /// <para>
    /// Ownership: the returned <see cref="WebSocket"/> is unowned; callers adopt it via
    /// <c>using var ws = pair.Ws;</c> so disposal is scoped to the test body. The join
    /// step is fail-fast — a non-ok reply throws (see <c>WebSocketTests.JoinTopicAsync</c>)
    /// and the socket is disposed inside this method's <c>catch</c> to avoid leaking a
    /// live connection. Tests that expect join to fail must hand-roll the two-part
    /// connect + join instead.
    /// </para>
    /// </summary>
    public static async Task<(WebSocket Ws, string Token)> ConnectAndJoinAsync(
        IWebFactoryFixture fixture,
        string systemId,
        CancellationToken cancellationToken)
    {
        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        var socketToken = await BaseEndpointTest.CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketTests.WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");

        var ws = await wsClientFactory.ConnectAsync(uri, cancellationToken);
        try
        {
            await WebSocketTests.JoinTopicAsync(ws, $"system:{systemId}", socketToken, cancellationToken);
        }
        catch
        {
            try { ws.Dispose(); } catch { /* swallow: caller sees the join failure */ }
            throw;
        }

        return (ws, socketToken);
    }
}
