using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

/// <summary>Unified resolver for Scylla connection values. Contact points and port honour
/// <see cref="ScyllaOverrideOptions"/> so integration tests can retarget a host-published
/// port; credentials come exclusively from <see cref="ISecretsStore"/>; the keyspace is
/// the per-node region identity sourced directly from
/// <see cref="PersistenceConfiguration.ScyllaKeyspace"/> (env → IOptions → resolver).</summary>
public interface IScyllaConfigResolver
{
    Task<string[]> GetContactPointsAsync(CancellationToken ct = default);

    /// <summary>Local datacenter for DC-aware routing. Defaults to <c>datacenter1</c>.</summary>
    Task<string> GetDatacenterAsync(CancellationToken ct = default);

    /// <summary>App-user username. <c>null</c> disables password auth on the client.</summary>
    Task<string?> GetUsernameAsync(CancellationToken ct = default);

    /// <summary>App-user password. Empty when the store row is unset.</summary>
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
