using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.Postgres;

public static class PostgresServiceCollectionExtensions
{
    private static readonly Action Registration = PersistenceRegistration.Create(PersistenceMode.ScyllaPostgres, AddPostgresPersistence);
    public static void Register() => Registration();

    private static IServiceCollection AddPostgresPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        return services
            .AddSingleton<IPostgresConnectionFactory, PostgresConnectionFactory>()
            .AddSingleton<ISecretsStore, PostgresSecretsStore>()
            .AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, AuthTokenRevocationRepository>()
            .AddHostedService<PostgresMigrationService>();
    }
}