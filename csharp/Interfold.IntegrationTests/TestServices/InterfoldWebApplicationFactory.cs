using Interfold.Api.Services;
using Interfold.Api.Socket;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Microsoft.AspNetCore.Hosting;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Interfold.IntegrationTests.TestServices;

public class InterfoldWebApplicationFactory : WebApplicationFactory<Program>
{
    // Live-reloadable override at the top of the config root. WithConfiguration writes fire
    // the reload token so IOptionsMonitor<T> consumers see updates; values snapshotted at
    // WebApplication.Build() (CORS, OAuth, IOptions, JWT registration) still need a fresh
    // factory.
    private readonly FactoryConfigurationProvider _configProvider = new();
    private static readonly ConditionalWeakTable<HttpClient, InterfoldWebApplicationFactory> ClientFactories = new();
    // Pinned instance so every service-provider root the factory constructs resolves the
    // SAME bus. Under TUnit's parallel WS suite, multiple roots would otherwise resolve
    // distinct InProcessEventBus instances and drop subscriptions.
    public InProcessEventBus EventBus { get; } = new();

    public FakeTimeProvider TimeProvider { get; } = new();
    public string DisplayName { get; private set; }

    /// <summary>Backend this factory was constructed for.</summary>
    public PersistenceMode PersistenceMode { get; }

    /// <summary>When non-null, TestHttpClientFactory chains a recording handler that captures
    /// outbound URI + Host header before dispatch. Factory is PerTestSession, so tests that
    /// set this MUST be [NotInParallel] under a shared key.</summary>
    public ConcurrentQueue<RecordedHttpCall>? OutboundHttpUriRecorder { get; set; }

    public InterfoldWebApplicationFactory(
        PersistenceMode persistenceMode,
        string? displayName = null,
        bool seedInMemorySecretsFromFactoryConfig = true)
    {
        PersistenceMode = persistenceMode;
        var persistenceType = persistenceMode.ToWire();
        DisplayName = displayName ?? persistenceType;
        _configProvider.Set("OCTOCON_PERSISTENCE", persistenceType);

        // In-memory runs drive the production env-var seed path via IConfiguration. Keys use
        // the `:`-form (the post-normalisation shape EnvironmentVariablesConfigurationProvider
        // produces from `OCTOCON_INMEMORY_SECRETS_SEED__*`) so SeedFromConfig looks them up.
        // seedInMemorySecretsFromFactoryConfig=false is for the env-var regression test that
        // must exercise the real Environment.SetEnvironmentVariable path unshadowed.
        if (seedInMemorySecretsFromFactoryConfig && persistenceMode == PersistenceMode.InMemory)
        {
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:ENCRYPTION_PEPPER",           "TEST");
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_ES256_PRIVATE_PEM",  TestDbCredentials.JwtEs256PrivateKeyPem);
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:AUTH_DEEP_LINK_SECRET",       TestDbCredentials.DeepLinkSecret);
            _configProvider.Set("OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_RSA256_PRIVATE_PEM", TestDbCredentials.JwtRsa256PrivateKeyPem);
        }
    }

    public override string ToString() => DisplayName;

    /// <summary>Writes into the top-priority configuration layer and fires the reload token.
    /// IOptionsMonitor consumers see the update; startup-snapshot consumers (CORS, OAuth,
    /// IOptions, JWT registration) still need a fresh factory.</summary>
    public InterfoldWebApplicationFactory WithConfiguration(string key, string? value)
    {
        _configProvider.Set(key, value);
        return this;
    }

    public new HttpClient CreateClient()
    {
        return TrackClient(base.CreateClient());
    }

    public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        return TrackClient(base.CreateClient(options));
    }

    public new HttpClient CreateDefaultClient(params DelegatingHandler[] handlers)
    {
        return TrackClient(base.CreateDefaultClient(handlers));
    }

    internal static bool TryGetFactory(HttpClient client, out InterfoldWebApplicationFactory factory)
    {
        return ClientFactories.TryGetValue(client, out factory!);
    }

    internal string CreateToken(string systemId)
    {
        // OptionsMonitor.CurrentValue returns the cached instance patched by SecretsPreBuildLoader
        // + AuthenticationSecretsPostConfigure; IConfiguration.Get<T>() would bypass those patches.
        var authConfig = Services.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>().CurrentValue;

        // Plug the same PEM the fixtures seeded into internal.secrets. Safe to mutate on the
        // cached instance: PostConfigure has already run and every consumer reads via this monitor.
        authConfig.JwtEs256PrivateKeyPem = TestDbCredentials.JwtEs256PrivateKeyPem;

        if (string.IsNullOrWhiteSpace(authConfig.JwtAuthority))
            authConfig.JwtAuthority = "test-authority";

        var jti = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(1);

        // Middleware requires scoped `{region}:{rawId}` sub; Compose is idempotent, and Nam
        // is the ParseScyllaKeyspace default for null/empty input.
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, systemId);

        return AuthHelper.CreateToken(authConfig, expiresAt, now, new(jti), scoped.AsSystemId());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Push snapshot via UseSetting so Program.cs sees values at DI-registration time
        // (AddInterfoldCluster, JWT registration, etc. read IConfiguration before
        // ConfigureAppConfiguration callbacks fire).
        foreach (var pair in _configProvider.Snapshot())
        {
            builder.UseSetting(pair.Key, pair.Value);
        }

        // Also wire the provider as the last config source so runtime WithConfiguration
        // writes fire the reload token; IOptionsMonitor<T> consumers pick them up in place.
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.Add(new FactoryConfigurationSource(_configProvider));
        });

        builder.ConfigureServices(x =>
        {
            x.AddSingleton(this);
            x.Replace(ServiceDescriptor.Singleton<IHttpClientFactory, TestHttpClientFactory>());
            x.Replace(ServiceDescriptor.Singleton(new SocketJoinRateLimiter(TimeProvider)));
            // Pinned clock keeps idempotency hashes stable across retries.
            x.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider));
            // See EventBus property — same instance across every service-provider root.
            x.Replace(ServiceDescriptor.Singleton<IClusterEventBus>(EventBus));
            x.Replace(ServiceDescriptor.Singleton(EventBus));

            // DB-backed runs bootstrap once via SharedDbFixture.WaitForResourcesAsync; strip
            // the migration hosted services so heavy DDL doesn't replay on every rebuild.
            // In-memory has no migrations to strip and seeds via IConfiguration directly.
            if (PersistenceMode != PersistenceMode.InMemory)
            {
                RemoveHostedService<PostgresMigrationService>(x);
                RemoveHostedService<ScyllaMigrationService>(x);
            }
        });
        
        base.ConfigureWebHost(builder);
    }

    /// <summary>Drops every <see cref="IHostedService"/> registration whose implementation
    /// type is <typeparamref name="TService"/>.</summary>
    private static void RemoveHostedService<TService>(IServiceCollection services)
        where TService : class, IHostedService
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(TService))
            {
                services.RemoveAt(i);
            }
        }
    }

    private HttpClient TrackClient(HttpClient client)
    {
        ClientFactories.Remove(client);
        ClientFactories.Add(client, this);
        return client;
    }
    
    public class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly InterfoldWebApplicationFactory _factory;

        public TestHttpClientFactory(InterfoldWebApplicationFactory factory)
        {
            _factory = factory;
        }

        public HttpClient CreateClient(string name)
        {
            // Recorder is opt-in per test; unset (default) → no handler, zero overhead.
            var recorder = _factory.OutboundHttpUriRecorder;
            return recorder is null
                ? _factory.CreateDefaultClient()
                : _factory.CreateDefaultClient(new RecordingDelegatingHandler(recorder));
        }
    }

    /// <summary>Captured outbound URI + Host header (TestHost's inner Request.Host isn't reliable).</summary>
    public sealed record RecordedHttpCall(Uri Uri, string? HostHeader);

    /// <summary>Records outbound URI + Host header before TestServer dispatches.</summary>
    private sealed class RecordingDelegatingHandler(
        ConcurrentQueue<RecordedHttpCall> sink) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                sink.Enqueue(new RecordedHttpCall(request.RequestUri, request.Headers.Host));
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Factory-lifetime configuration source that survives host rebuilds.</summary>
    private sealed class FactoryConfigurationSource(FactoryConfigurationProvider provider)
        : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    /// <summary>Mutable in-memory provider that fires OnReload on every Set so IOptionsMonitor
    /// consumers re-read the bound value; access is lock-guarded.</summary>
    private sealed class FactoryConfigurationProvider : ConfigurationProvider
    {
        private readonly Lock _lock = new();

        public override void Set(string key, string? value)
        {
            lock (_lock)
            {
                Data[key] = value;
            }
            OnReload();
        }

        /// <summary>Stable snapshot for callers that need to forward pairs to another sink
        /// (e.g. UseSetting for startup-snapshot reads).</summary>
        public IReadOnlyList<KeyValuePair<string, string?>> Snapshot()
        {
            lock (_lock)
            {
                return Data.ToArray();
            }
        }
    }
}
