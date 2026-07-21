using Cassandra;
using Interfold.Contracts.Ids;
using Interfold.Infrastructure.Scylla;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Shared preamble for direct-CQL Scylla integration tests — the ones that must synthesise
/// a pathological row via the cluster session (partial migration / manual fixup / schema
/// drift) before driving the production repository against it. Every such test resolves
/// the same trio out of the factory (<see cref="IScyllaKeyspaceResolver"/>,
/// <see cref="IScyllaSessionProvider"/>) and derives the same trio of per-system primitives
/// (<paramref name="systemId"/> → normalized string, regional keyspace, live
/// <see cref="ISession"/>).
///
/// <para>
/// Previously duplicated verbatim in <c>ScyllaAlterRepositoryUdtNullTests</c> and
/// <c>ScyllaFrontingRepositoryNullTimeStartTests</c>; extract point picked out by the R2
/// dedup plan (D11.c).
/// </para>
/// </summary>
internal static class ScyllaDirectHarness
{
    internal sealed record Context(
        ISession Session,
        string Keyspace,
        string NormalizedSystemId);

    public static async Task<Context> ResolveAsync(IWebFactoryFixture fixture, SystemId systemId)
    {
        var services = fixture.Factory.Services;
        var keyspaceResolver = services.GetRequiredService<IScyllaKeyspaceResolver>();
        var sessionProvider = services.GetRequiredService<IScyllaSessionProvider>();

        var session = await sessionProvider.GetSessionAsync();
        var keyspace = keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var normalizedSystemId = keyspaceResolver.NormalizeSystemId(systemId);

        return new Context(session, keyspace, normalizedSystemId);
    }
}
