using Interfold.Socket.Api.Helpers;
using Interfold.Socket.Api.Socket;

namespace Interfold.Socket.Api.DependencyInjection;

/// <summary>Socket feature module — owns the Phoenix-protocol WebSocket transport
/// (<c>WebSocketHandler</c> + <c>WebSocketInitialization</c> + <c>SocketEventPumpRunner</c>
/// + per-feature <c>*SocketEventHandlers</c>), the <c>SocketJoinRateLimiter</c> singleton,
/// the loopback named <see cref="HttpClient"/> that the endpoint-relay self-calls,
/// and the <c>/api/socket/websocket</c> route. Composed once from
/// <c>Interfold.Api.Host/Program.cs</c> alongside the other feature modules.</summary>
public static class SocketModuleServiceCollectionExtensions
{
    /// <summary>Registers the socket runtime: <c>SocketJoinRateLimiter</c> singleton +
    /// the <see cref="LoopbackHttpClient"/> named <see cref="HttpClient"/> with a permissive
    /// TLS-validation handler (the leaf PFX served by Kestrel has no loopback SAN, and the
    /// call site's <see cref="LoopbackHttpClient.IsLoopbackHost"/> guard pins outbound calls
    /// to loopback — see <c>WebSocketHandler.HandleEndpointProxyAsync</c>).</summary>
    public static IServiceCollection AddSocketModule(this IServiceCollection services)
    {
        services.AddSingleton<SocketJoinRateLimiter>();

        services.AddHttpClient(LoopbackHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            });

        return services;
    }

    /// <summary>Maps the anonymous <c>GET|CONNECT /api/socket/websocket</c> upgrade route
    /// to <see cref="WebSocketHandler.HandleUserSocketAsync"/>. Host must have called
    /// <see cref="WebSocketMiddlewareExtensions.UseWebSockets(IApplicationBuilder, WebSocketOptions)"/>
    /// first so the upgrade middleware is in the pipeline.</summary>
    public static IEndpointRouteBuilder MapSocketModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapMethods("/api/socket/websocket", ["GET", "CONNECT"], WebSocketHandler.HandleUserSocketAsync)
            .AllowAnonymous();
        return endpoints;
    }
}
