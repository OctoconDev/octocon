using Interfold.Api.Services;
using Interfold.Api.Services.Http;
using Interfold.Api.Services.ImportJobs;
using Interfold.Domain;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Api.SimplyPlural;

public static class SimplyPluralImportExtensions
{
    /// <summary>
    /// Registers only <see cref="ISimplyPluralImportService"/> — the pure import service
    /// with no HttpClient wiring and no async job-runner. Every caller that drives an
    /// import synchronously (SPDump, in-process SP import integration tests) should use
    /// this instead of <see cref="AddSimplyPluralImport"/> so they can plug their own
    /// <see cref="HttpClient"/> primary handler (stub, header-injecting proxy, etc.) and
    /// skip the queue consumer.
    /// </summary>
    public static IServiceCollection AddSimplyPluralImportCore(this IServiceCollection services)
    {
        services.AddSingleton<ISimplyPluralImportService, SimplyPluralImportService>();
        return services;
    }

    /// <summary>
    /// Full API-server wiring: the core service (via <see cref="AddSimplyPluralImportCore"/>),
    /// a named <see cref="HttpClient"/> for <see cref="HttpClientNames.SimplyPlural"/> with
    /// the shared <see cref="HttpLoggingHandler"/>, and the <see cref="IImportJobRunner"/>
    /// that dequeues async SP import jobs. Only Program.cs should call this; scaffolds and
    /// tests should prefer <see cref="AddSimplyPluralImportCore"/> and register their own
    /// HttpClient shape.
    /// </summary>
    public static IServiceCollection AddSimplyPluralImport(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientNames.SimplyPlural).AddHttpMessageHandler<HttpLoggingHandler>();
        services.AddSimplyPluralImportCore();
        services.AddSingleton<IImportJobRunner, SpImportJobRunner>();
        return services;
    }
}
