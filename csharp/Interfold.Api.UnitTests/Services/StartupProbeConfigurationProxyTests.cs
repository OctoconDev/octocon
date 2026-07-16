using Interfold.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Api.UnitTests.Services;

/// <summary>
/// Pins the disposal-safety contract that <see cref="StartupProbeConfigurationProxy"/>
/// exists to enforce. If any of these break, Program.cs's startup probe will silently
/// resurrect the "probe SP disposes the shared ConfigurationManager" bug and every
/// controller integration test will start failing again with
/// <c>ObjectDisposedException: 'ConfigurationManager'</c> at <c>WebApplicationFactory.CreateClient()</c>.
/// </summary>
public sealed class StartupProbeConfigurationProxyTests
{
    /// <summary>
    /// Core invariant: disposing an SP built off <see cref="StartupProbeConfigurationProxy.SwapInto"/>
    /// must leave the underlying <see cref="ConfigurationManager"/> usable. This is the
    /// direct regression pin for the ObjectDisposedException that broke every controller
    /// integration test — <c>WebApplicationFactory.ConfigureWebHost</c> tries to
    /// <c>Add</c> a source during <c>builder.Build()</c>, so if the probe kills the
    /// manager first the whole test stack collapses.
    /// </summary>
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

    /// <summary>
    /// Counter-example that documents WHY the proxy is necessary: without swap, the
    /// framework's <c>services.AddSingleton&lt;IConfiguration&gt;(_ =&gt; configuration)</c>
    /// registration is factory-shape so <see cref="ServiceProvider"/> captures the
    /// resolved instance and disposes it on tear-down. If this test ever starts
    /// passing (i.e. the framework changes ownership semantics), the proxy may be
    /// removable — until then, this is the shape of the bug the proxy defends against.
    /// </summary>
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

    /// <summary>
    /// Ordinary <see cref="IConfiguration"/> reads through the proxy must route to the
    /// same live provider stack the real manager holds; otherwise
    /// <c>AddInterfoldOptions</c>' <c>Configure&lt;IConfiguration&gt;</c> callbacks (which
    /// resolve IConfiguration and read keys from it) would see stale/empty values in the
    /// probe SP and Program.cs's startup snapshots would drift from what the real host
    /// binds a moment later.
    /// </summary>
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

    /// <summary>
    /// The proxy MUST NOT implement <see cref="IDisposable"/> — that's the entire
    /// mechanism that keeps DI's disposal-capture walker from taking ownership of the
    /// wrapped configuration. A well-meaning refactor that adds <c>IDisposable</c> for
    /// symmetry with the wrapped type would silently reintroduce the bug the whole class
    /// exists to prevent, so this test locks the interface set explicitly.
    /// </summary>
    [Test]
    public async Task Proxy_DoesNotImplementIDisposable()
    {
        // Reflection over the type (rather than `proxy is IDisposable` on the instance) so
        // the assertion still fires after any future refactor that removes `sealed` or
        // pulls the type through a base class — the DI disposal-capture walker checks the
        // resolved instance at runtime, not the compile-time type, so this test must too.
        var implements = typeof(IDisposable).IsAssignableFrom(typeof(StartupProbeConfigurationProxy));

        await Assert.That(implements).IsFalse()
            .Because("Adding IDisposable would put the proxy back in DI's disposal set — the entire point of the wrapper is to be invisible to that walker so the shared ConfigurationManager isn't taken down when the probe SP is torn down.");
    }
}
