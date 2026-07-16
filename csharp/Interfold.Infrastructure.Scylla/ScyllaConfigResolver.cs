using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// Unified resolution for Scylla connection values.
/// <list type="bullet">
///   <item>Contact points and port honour <see cref="ScyllaOverrideOptions"/> (bound from
///     <c>OCTOCON_SCYLLA_CONTACT_POINTS</c> and <c>OCTOCON_SCYLLA_PORT</c>) so integration
///     tests can point the client at a host-published port that differs from the
///     secrets-store cluster address. A <c>null</c> value on either override falls through
///     to the store row.</item>
///   <item>Credentials (username, password, datacenter) come exclusively from
///     <see cref="ISecretsStore"/> — the store is the single source of truth in every
///     non-test deployment.</item>
///   <item>The keyspace is the per-node region identity and is sourced directly from
///     <see cref="PersistenceConfiguration.ScyllaKeyspace"/>. There is no store fallback
///     (the row was dropped along with the matching <c>SecretsStoreKeys</c> entry) —
///     the value flows env → <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>
///     → this resolver → the cluster.</item>
/// </list>
/// </summary>
public interface IScyllaConfigResolver
{
    /// <summary>Contact-point hosts, in order of preference. Override wins over the store row.</summary>
    Task<string[]> GetContactPointsAsync(CancellationToken ct = default);

    /// <summary>Local datacenter name for DC-aware routing. Defaults to <c>datacenter1</c>.</summary>
    Task<string> GetDatacenterAsync(CancellationToken ct = default);

    /// <summary>App-user Scylla username. <c>null</c> disables password authentication on the client.</summary>
    Task<string?> GetUsernameAsync(CancellationToken ct = default);

    /// <summary>App-user Scylla password. Empty string when the store row is unset.</summary>
    Task<string> GetPasswordAsync(CancellationToken ct = default);

    /// <summary>Regional keyspace wire value (e.g. <c>nam</c>, <c>eur</c>).</summary>
    string GetKeyspace();

    /// <summary>Scylla TCP port. Override wins over the store row; falls back to <c>9042</c>.</summary>
    Task<int> GetPortAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IScyllaConfigResolver" />
public sealed class ScyllaConfigResolver : IScyllaConfigResolver
{
    private readonly ISecretsStore _secretsStore;
    private readonly ScyllaOverrideOptions _overrides;
    private readonly PersistenceConfiguration _persistence;

    public ScyllaConfigResolver(
        ISecretsStore secretsStore,
        IOptions<ScyllaOverrideOptions> overrides,
        IOptions<PersistenceConfiguration> persistence)
    {
        _secretsStore = secretsStore;
        _overrides = overrides.Value;
        _persistence = persistence.Value;
    }

    public async Task<string[]> GetContactPointsAsync(CancellationToken ct = default)
    {
        if (_overrides.ContactPoints is { Count: > 0 } configOverride)
        {
            return configOverride.ToArray();
        }

        var raw = await _secretsStore.GetAsync(SecretsStoreKeys.ScyllaContactPoints, ct);
        return string.IsNullOrWhiteSpace(raw)
            ? ["127.0.0.1"]
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<string> GetDatacenterAsync(CancellationToken ct = default)
    {
        return await _secretsStore.GetAsync(SecretsStoreKeys.ScyllaLocalDatacenter, ct)
            ?? "datacenter1";
    }

    public async Task<string?> GetUsernameAsync(CancellationToken ct = default)
    {
        return await _secretsStore.GetAsync(SecretsStoreKeys.ScyllaUsername, ct);
    }

    public async Task<string> GetPasswordAsync(CancellationToken ct = default)
    {
        return await _secretsStore.GetAsync(SecretsStoreKeys.ScyllaPassword, ct)
            ?? string.Empty;
    }

    public string GetKeyspace()
    {
        // OCTOCON_SCYLLA_KEYSPACE flows env -> ApplyPersistence -> PersistenceConfiguration
        // -> IOptions<PersistenceConfiguration>. The wire value here is the same string
        // the migration service and IRegionContext consumers use as a keyspace identifier.
        return _persistence.ScyllaKeyspace.ToWire();
    }

    public async Task<int> GetPortAsync(CancellationToken ct = default)
    {
        if (_overrides.Port is { } port)
        {
            return port;
        }

        var raw = await _secretsStore.GetAsync(SecretsStoreKeys.ScyllaPort, ct);
        return int.TryParse(raw, out var storePort) ? storePort : 9042;
    }
}
