using System;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.DependencyInjection;

public static class PersistenceRegistration
{
    public static Action Create(PersistenceMode mode, Func<IServiceCollection, PersistenceConfiguration, IServiceCollection> factory)
    {
        var registration = new Lazy<bool>(() =>
        {
            ServiceCollectionExtensions.AddPersistenceMode(mode, factory);
            return true;
        });

        return () => _ = registration.Value;
    }
}
