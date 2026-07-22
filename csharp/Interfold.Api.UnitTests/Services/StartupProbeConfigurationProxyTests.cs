using Interfold.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Api.UnitTests.Services;

// Pins the disposal-safety contract StartupProbeConfigurationProxy enforces. If any
// break, Program.cs's startup probe silently resurrects the "probe SP disposes the
// shared ConfigurationManager" bug and controller integration tests fail with
// ObjectDisposedException at WebApplicationFactory.CreateClient().
public sealed class StartupProbeConfigurationProxyTests
{
    // Core invariant: disposing an SP built off SwapInto leaves the underlying
    // ConfigurationManager usable — WebApplicationFactory.ConfigureWebHost still needs
    // to Add sources during builder.Build().
    [Test]
    public async Task ProbeDispose_DoesNotDisposeUnderlyingConfigurationManager()
    {
        var configManager = new ConfigurationManager();
        configManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["probe:sentinel"] = "before-dispose",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_ => configManager);

        var probeServices = StartupProbeConfigurationProxy.SwapInto(services, configManager);
        using (var probe = probeServices.BuildServiceProvider(validateScopes: false))
        {
            _ = probe.GetRequiredService<IConfiguration>();
        }

        ((IConfigurationBuilder)configManager).Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["probe:sentinel"] = "after-dispose" }.ToList(),
        });

        await Assert.That(configManager["probe:sentinel"]).IsEqualTo("after-dispose")
            .Because("The proxy MUST prevent probe SP disposal from tearing down the shared ConfigurationManager — otherwise this Add() would throw ObjectDisposedException, which is exactly the fault every integration test hit before this fix.");
    }

    // Counter-example showing why the proxy is necessary: without swap, DI's factory
    // registration captures the resolved IConfiguration and disposes it on tear-down.
    // If this test ever starts passing, revisit whether the proxy is still needed.
    [Test]
    public async Task ProbeDispose_WithoutProxy_DoesDisposeUnderlyingConfigurationManager()
    {
        var configManager = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_ => configManager);

        using (var probe = services.BuildServiceProvider(validateScopes: false))
        {
            _ = probe.GetRequiredService<IConfiguration>();
        }

        Exception? thrown = null;
        try
        {
            ((IConfigurationBuilder)configManager).Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource());
        }
        catch (ObjectDisposedException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull()
            .Because("Framework-registered IConfiguration is factory-shape and captured for disposal — this is the exact ownership chain the proxy breaks. If .NET ever changes this, revisit whether the proxy is still needed.");
    }

    // Reads through the proxy must route to the live provider stack — otherwise
    // Program.cs's startup snapshots drift from what the real host binds moments later.
    [Test]
    public async Task Proxy_ForwardsReadsToUnderlyingConfiguration()
    {
        var configManager = new ConfigurationManager();
        configManager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["probe:key"] = "live-value",
            ["probe:nested:child"] = "42",
        });

        var proxy = new StartupProbeConfigurationProxy(configManager);

        using IDisposable _ = Assert.Multiple();
        await Assert.That(proxy["probe:key"]).IsEqualTo("live-value")
            .Because("The proxy is a thin forwarder — every indexer read must see the same value the wrapped ConfigurationManager would return.");
        await Assert.That(proxy.GetSection("probe:nested")["child"]).IsEqualTo("42")
            .Because("Sub-section reads must also forward, otherwise options binders that walk .GetSection(...) chains would miss configured values in the probe SP.");
    }

    // The proxy MUST NOT implement IDisposable — that's the mechanism that keeps DI's
    // disposal-capture walker from taking ownership of the wrapped configuration.
    [Test]
    public async Task Proxy_DoesNotImplementIDisposable()
    {
        // Reflect over the type so this catches a refactor that removes `sealed` or
        // pulls the type through a disposable base class.
        var implements = typeof(IDisposable).IsAssignableFrom(typeof(StartupProbeConfigurationProxy));

        await Assert.That(implements).IsFalse()
            .Because("Adding IDisposable would put the proxy back in DI's disposal set — the entire point of the wrapper is to be invisible to that walker so the shared ConfigurationManager isn't taken down when the probe SP is torn down.");
    }
}
