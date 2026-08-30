using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Interfold.AppHost;

/// <summary>Configuration for <see cref="TestBenchReadyEmitter"/>. Ports are the host-mapped
/// values <see cref="InterfoldAppHost.Configure"/> allocated; resource names are the same
/// Aspire resource ids passed to <c>builder.AddContainer</c> so
/// <see cref="ResourceNotificationService.WaitForResourceAsync(string, string, System.Threading.CancellationToken)"/>
/// matches. A null resource name means the backend is not included this run.</summary>
internal sealed record TestBenchReadyEmitterOptions(
    int PostgresPort,
    int? ScyllaPort,
    int? CassandraPort,
    string PostgresResourceName,
    string? ScyllaResourceName,
    string? CassandraResourceName);

/// <summary>Bench-mode-only hosted service that waits for every included DB resource to
/// reach <see cref="KnownResourceStates.Running"/>, writes a machine-readable readiness
/// line to stdout, then requests app shutdown. TestBenchCoordinator's launcher reads the
/// line and knows the persistent-lifetime containers are up; the AppHost process is
/// allowed to exit because the containers survive it.</summary>
internal sealed class TestBenchReadyEmitter : BackgroundService
{
    // Sentinel string TestBenchCoordinator matches against on stdout. Keep in sync with
    // TestBenchCoordinator.ReadyLinePrefix.
    private const string ReadyLinePrefix = "[test-bench] ready";

    private readonly ResourceNotificationService _notifications;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TestBenchReadyEmitterOptions _options;
    private readonly ILogger<TestBenchReadyEmitter> _logger;

    public TestBenchReadyEmitter(
        ResourceNotificationService notifications,
        IHostApplicationLifetime lifetime,
        TestBenchReadyEmitterOptions options,
        ILogger<TestBenchReadyEmitter> logger)
    {
        _notifications = notifications;
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitRunningOrThrowAsync(_options.PostgresResourceName, stoppingToken).ConfigureAwait(false);

            if (_options.ScyllaResourceName is not null)
                await WaitRunningOrThrowAsync(_options.ScyllaResourceName, stoppingToken).ConfigureAwait(false);

            if (_options.CassandraResourceName is not null)
                await WaitRunningOrThrowAsync(_options.CassandraResourceName, stoppingToken).ConfigureAwait(false);

            var parts = new List<string> { $"pg={_options.PostgresPort}" };
            if (_options.ScyllaPort is int sp) parts.Add($"scylla={sp}");
            if (_options.CassandraPort is int cp) parts.Add($"cassandra={cp}");
            Console.WriteLine($"{ReadyLinePrefix} {string.Join(' ', parts)}");
            await Console.Out.FlushAsync(stoppingToken).ConfigureAwait(false);

            // Persistent-lifetime containers survive; shutting down releases the launcher.
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TestBenchReadyEmitter failed to observe DB resources; launcher will exit.");
            // Surface the failure on stderr so TestBenchCoordinator's exit-before-ready
            // path includes the FailedToStart cause instead of an empty 10-minute timeout.
            Console.Error.WriteLine($"[test-bench] ready-emitter failed: {ex.Message}");
            await Console.Error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            _lifetime.StopApplication();
            throw;
        }
    }

    /// <summary>Races Running against FailedToStart so a wedged Scylla (typical on
    /// Docker Desktop with &lt;8 GiB RAM) fails the launcher in seconds instead of hanging
    /// until <c>LauncherTimeout</c>.</summary>
    private async Task WaitRunningOrThrowAsync(string resourceName, CancellationToken ct)
    {
        var running = _notifications.WaitForResourceAsync(resourceName, KnownResourceStates.Running, ct);
        var failed = _notifications.WaitForResourceAsync(resourceName, KnownResourceStates.FailedToStart, ct);
        var completed = await Task.WhenAny(running, failed).ConfigureAwait(false);
        if (completed == failed)
        {
            await failed.ConfigureAwait(false);
            var detail = await AspireResourceFailureDiagnostics
                .DescribeAsync(resourceName, AppHostRepoPaths.ResolveRepoRoot(), ct)
                .ConfigureAwait(false);
            throw new InvalidOperationException(detail);
        }

        await running.ConfigureAwait(false);
    }
}
