using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Interfold.Api.Services;

/// <summary>
/// Non-owning <see cref="IConfigurationRoot"/> proxy that forwards to the wrapped instance
/// but does NOT implement <see cref="IDisposable"/>, so DI's disposal-capture walker skips
/// it. Used only by Program.cs's throw-away startup-probe service provider via
/// <see cref="SwapInto"/> to prevent probe-SP disposal from also disposing the shared
/// <see cref="ConfigurationManager"/> owned by
/// <see cref="Microsoft.AspNetCore.Builder.WebApplicationBuilder"/> — which otherwise breaks
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>'s later
/// <c>ConfigureAppConfiguration</c> with <c>ObjectDisposedException: 'ConfigurationManager'</c>.
/// Not for general use; do not register in <c>builder.Services</c>.
/// </summary>
internal sealed class StartupProbeConfigurationProxy(IConfigurationRoot inner) : IConfigurationRoot
{
    public string? this[string key]
    {
        get => inner[key];
        set => inner[key] = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();

    public IChangeToken GetReloadToken() => inner.GetReloadToken();

    public IConfigurationSection GetSection(string key) => inner.GetSection(key);

    public void Reload() => inner.Reload();

    public IEnumerable<IConfigurationProvider> Providers => inner.Providers;

    /// <summary>
    /// Returns a clone of <paramref name="source"/> with the <see cref="IConfiguration"/>
    /// and <see cref="IConfigurationRoot"/> registrations replaced by a shared proxy over
    /// <paramref name="root"/>; all other descriptors are copied verbatim. Disposing the
    /// probe SP built from the clone leaves <paramref name="root"/> intact.
    /// </summary>
    public static IServiceCollection SwapInto(IServiceCollection source, IConfigurationRoot root)
    {
        var proxy = new StartupProbeConfigurationProxy(root);
        IServiceCollection clone = new ServiceCollection();

        foreach (var descriptor in source)
        {
            if (descriptor.ServiceType == typeof(IConfiguration))
            {
                clone.AddSingleton<IConfiguration>(proxy);
            }
            else if (descriptor.ServiceType == typeof(IConfigurationRoot))
            {
                clone.AddSingleton<IConfigurationRoot>(proxy);
            }
            else
            {
                clone.Add(descriptor);
            }
        }

        return clone;
    }
}
