using System.Net.WebSockets;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Mint-token + connect + phx_join scaffolding for the Phoenix-style websocket
/// surface. Both helpers fail fast if the join step returns non-ok; tests that expect a
/// join failure must hand-roll setup instead.</summary>
public static class WebSocketHarness
{
    /// <summary>Opens two sockets and joins <c>system:{id}</c> on each. Returned sockets
    /// are unowned — adopt with <c>using var</c> at the call site.</summary>
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

        var firstUri = new Uri(WebSocketExtensions.WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={firstToken}");
        var secondUri = new Uri(WebSocketExtensions.WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={secondToken}");

        var firstWs = await wsClientFactory.ConnectAsync(firstUri, cancellationToken);
        var secondWs = await wsClientFactory.ConnectAsync(secondUri, cancellationToken);

        try
        {
            await WebSocketExtensions.JoinTopicAsync(firstWs, $"system:{firstSystemId}", firstToken, cancellationToken);
            await WebSocketExtensions.JoinTopicAsync(secondWs, $"system:{secondSystemId}", secondToken, cancellationToken);
        }
        catch
        {
            // Dispose to avoid leaking sockets before caller adopts ownership; swallow so
            // the original join failure surfaces.
            try { firstWs.Dispose(); } catch { }
            try { secondWs.Dispose(); } catch { }
            throw;
        }

        return (firstWs, secondWs, firstToken, secondToken);
    }

    /// <summary>Single-party sister of <see cref="ConnectPairAndJoinAsync"/>. Returned
    /// socket is unowned.</summary>
    public static async Task<(WebSocket Ws, string Token)> ConnectAndJoinAsync(
        IWebFactoryFixture fixture,
        string systemId,
        CancellationToken cancellationToken)
    {
        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        var socketToken = await BaseEndpointTest.CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketExtensions.WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");

        var ws = await wsClientFactory.ConnectAsync(uri, cancellationToken);
        try
        {
            await WebSocketExtensions.JoinTopicAsync(ws, $"system:{systemId}", socketToken, cancellationToken);
        }
        catch
        {
            try { ws.Dispose(); } catch { }
            throw;
        }

        return (ws, socketToken);
    }
}
