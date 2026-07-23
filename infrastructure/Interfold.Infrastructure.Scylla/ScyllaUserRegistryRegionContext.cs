using System.Collections.Concurrent;
using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

/// <summary><see cref="IRegionContext"/> backed by <c>global.user_registry</c> with a
/// bounded in-process cache. Falls back to the locally configured default region when
/// the registry row is absent or the Scylla session is not yet ready.</summary>
public sealed class ScyllaUserRegistryRegionContext : IRegionContext
{
    private const int MaxCacheSize = 1024;

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaUserRegistryRegionContext> _logger;
    private readonly ConcurrentDictionary<string, ScyllaKeyspace> _cache = new(StringComparer.Ordinal);
    private readonly Lazy<ScyllaKeyspace> _currentRegion;

    public ScyllaKeyspace CurrentRegion => _currentRegion.Value;

    public ScyllaUserRegistryRegionContext(
        IScyllaSessionProvider sessionProvider,
        IOptions<PersistenceConfiguration> options,
        ILogger<ScyllaUserRegistryRegionContext> logger)
    {
        _sessionProvider = sessionProvider;
        _options = options.Value;
        _logger = logger;
        // ScyllaKeyspace on the options is the enum-typed single source of truth.
        _currentRegion = new Lazy<ScyllaKeyspace>(() => _options.ScyllaKeyspace);
    }

    public ScyllaKeyspace ResolveUserRegion(SystemId systemId) => ResolveUserRegionCore(systemId);

    // String-typed core shared with the async / RegisterRegion siblings; the interface
    // only exposes the typed SystemId overload.
    private ScyllaKeyspace ResolveUserRegionCore(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return CurrentRegion;

        var (cacheKey, handle) = HandleForLookup(systemId);

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Sync path: driver never marshals back to a captured SynchronizationContext,
        // so GetAwaiter().GetResult() cannot deadlock a request thread.
        try
        {
            var region = LookupAsync(handle, systemId).GetAwaiter().GetResult();
            if (TryParseRegion(cacheKey, region, out var parsed))
            {
                StoreInCache(cacheKey, parsed);
                return parsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Region lookup for system {SystemId} failed; falling back to default region.", cacheKey);
        }

        return CurrentRegion;
    }

    /// <summary>Async variant for hot paths that already have an async context.
    /// Falls back to <see cref="CurrentRegion"/> when the registry row is absent.</summary>
    public async Task<ScyllaKeyspace> ResolveUserRegionAsync(
        string systemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return CurrentRegion;

        var (cacheKey, handle) = HandleForLookup(systemId);

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var region = await LookupAsync(handle, systemId, cancellationToken);
        if (TryParseRegion(cacheKey, region, out var parsed))
        {
            StoreInCache(cacheKey, parsed);
            return parsed;
        }

        return CurrentRegion;
    }

    /// <summary>Populates the cache for a user whose home region is already known
    /// (e.g. after account registration).</summary>
    public void RegisterRegion(string systemId, ScyllaKeyspace region)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return;

        var (cacheKey, _) = HandleForLookup(systemId);
        StoreInCache(cacheKey, region);
    }

    private bool TryParseRegion(string systemId, string? raw, out ScyllaKeyspace region)
    {
        region = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            region = EnumWireExtensions.ParseScyllaKeyspace(raw);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // Never poison the cache or crash the caller on a corrupt registry row.
            _logger.LogWarning(ex,
                "Registry region '{Region}' for system {SystemId} is not a known keyspace; falling back to default region.",
                raw, systemId);
            return false;
        }
    }

    private async Task<string?> LookupAsync(
        UserRegistryLookup? handle,
        string originalInput,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            // handle == null when TryParse rejected the input — route the whole original
            // string through user_id so a rejected handle stays an opaque bare-id lookup.
            var (column, value) = handle switch
            {
                { Kind: UserRegistryLookupKind.Username } h => (UserRegistryLookupColumn.Username, h.RawId),
                { Kind: UserRegistryLookupKind.Discord } h => (UserRegistryLookupColumn.DiscordId, h.RawId),
                { Kind: UserRegistryLookupKind.Region } h => (UserRegistryLookupColumn.UserId, h.RawId),
                { Kind: UserRegistryLookupKind.Id } h => (UserRegistryLookupColumn.UserId, h.RawId),
                _ => (UserRegistryLookupColumn.UserId, originalInput),
            };

            var query = new SimpleStatement(
                $"SELECT region FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE {ColumnName(column)} = ? LIMIT 1",
                value);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row?.GetValue<string>("region");
        }, _options, cancellationToken, _logger);
    }

    private void StoreInCache(string key, ScyllaKeyspace region)
    {
        if (_cache.Count >= MaxCacheSize)
        {
            // Coarse eviction — clear on overflow. Good enough until memory pressure warrants a true LRU.
            _cache.Clear();
        }

        _cache[key] = region;
    }

    // Cache key collapses id-shaped inputs onto RawId ("abcdefg", "nam:abcdefg", "id:abcdefg"
    // all identify the same user_registry row); username / Discord keys keep their prefix.
    private static (string cacheKey, UserRegistryLookup? handle) HandleForLookup(string systemId)
    {
        if (!UserRegistryLookup.TryParse(systemId, out var parsed))
        {
            return (systemId, null);
        }

        var cacheKey = parsed.Kind switch
        {
            UserRegistryLookupKind.Region => parsed.RawId,
            UserRegistryLookupKind.Id => parsed.RawId,
            _ => parsed.OriginalValue,
        };

        return (cacheKey, parsed);
    }

    // Ties the UserRegistryLookupKind switch to the CQL column universe so a new lookup
    // kind can't drift from the SQL builder.
    private enum UserRegistryLookupColumn
    {
        Username,
        DiscordId,
        UserId,
    }

    private static string ColumnName(UserRegistryLookupColumn column) => column switch
    {
        UserRegistryLookupColumn.Username => "username",
        UserRegistryLookupColumn.DiscordId => "discord_id",
        UserRegistryLookupColumn.UserId => "user_id",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown user-registry lookup column."),
    };
}
