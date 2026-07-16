using System.Collections.Concurrent;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// IRegionContext backed by global.user_registry, with a bounded in-process cache and
/// fallback to the locally configured default region when the registry row is absent or
/// the Scylla session is not yet available.
///
/// <para>
/// Public resolution APIs are typed as <see cref="ScyllaKeyspace"/>; the registry stores
/// lowercase region strings and the cache stays string-keyed with the legacy-prefix
/// stripping internal to this type.
/// </para>
/// </summary>
public sealed class ScyllaUserRegistryRegionContext : IRegionContext
{
    // Bounded LRU: keep most-recently-used entries simple via ConcurrentDictionary.
    // A higher-fidelity LRU eviction policy can be added later if memory pressure warrants it.
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
        // PersistenceConfiguration.ScyllaKeyspace is the enum-typed single source of truth
        // for the per-node region identity; the resolver's GetKeyspace() call would parse
        // the same wire value back to this enum, so read it directly and skip the round-trip.
        _currentRegion = new Lazy<ScyllaKeyspace>(() => _options.ScyllaKeyspace);
    }

    public ScyllaKeyspace ResolveUserRegion(SystemId systemId) => ResolveUserRegionCore(systemId);

    // Private string-typed core so the internal helpers (HandleForLookup, LookupAsync,
    // StoreInCache, TryParseRegion) and the still-string-typed public sibling APIs
    // (ResolveUserRegionAsync, RegisterRegion) can all share one implementation while the
    // interface surface presents only the typed SystemId overload.
    private ScyllaKeyspace ResolveUserRegionCore(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return CurrentRegion;

        // A parseable UserRegistryLookup carries an explicit Kind so LookupAsync knows
        // which registry column to hit; an unparseable input (unknown prefix, or
        // bare-prefix like "nam:") falls through to the "opaque bare id" branch inside
        // LookupAsync with the whole systemId as the query value — matches the
        // strict-rejection contract on UserRegistryLookup.TryParse.
        var (cacheKey, handle) = HandleForLookup(systemId);

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Synchronous path: attempt a best-effort lookup on the calling thread.
        // This keeps the interface non-async while avoiding a thread-pool deadlock on most
        // callers that are already async. GetAwaiter().GetResult() is safe here because
        // the backing Cassandra driver never marshals back to the same synchronization
        // context that an ASP.NET request would occupy.
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
            // Any exception (session not yet ready, network error) falls through to default.
            _logger.LogWarning(ex, "Region lookup for system {SystemId} failed; falling back to default region.", cacheKey);
        }

        return CurrentRegion;
    }

    /// <summary>
    /// Asynchronous variant to be used in hot paths that already have an async context.
    /// Falls back to <see cref="CurrentRegion"/> when the registry row is absent.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

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
            // A corrupt registry row must not poison the cache or crash the caller —
            // log loudly and let the caller fall back to the default region.
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

            // Fallback shape (handle == null) happens when UserRegistryLookup.TryParse
            // rejected the input as unparseable — bare-prefix like "nam:", or an unknown
            // non-region prefix like "xxx:abcdefg". We deliberately query user_id with
            // the WHOLE original input so the strict-rejection contract holds: a
            // rejected handle becomes an opaque bare-id lookup, never a silent
            // prefix-strip.
            //
            // The column selector is a typed UserRegistryLookupColumn rather than a magic
            // string. The CQL text still needs to interpolate the raw column name, so
            // ColumnName produces it in exactly one spot — a new lookup kind can't drift
            // from the SQL builder because the compiler forces the enum branch first.
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
            // Simple eviction: clear on overflow to avoid unbounded growth.
            // A proper LRU would use a linked list; this is sufficient for phase-3 scope.
            _cache.Clear();
        }

        _cache[key] = region;
    }

    /// <summary>
    /// Turn an incoming lookup input into a (cache key, parsed handle) pair.
    /// <see cref="UserRegistryLookup.TryParse"/> is the single source of truth for the
    /// routing table; this helper just picks the cache key so different-shape inputs
    /// referring to the same user (bare id, region-scoped id, explicit <c>id:</c>
    /// prefix) collapse onto the same cache entry while username / Discord lookups keep
    /// their own key space (there's no ambiguity — a username string can't collide with
    /// a system id in the same 7-char alphabet). Unparseable input keeps its whole
    /// original string as the cache key so a rejected handle round-trips deterministically.
    /// </summary>
    private static (string cacheKey, UserRegistryLookup? handle) HandleForLookup(string systemId)
    {
        if (!UserRegistryLookup.TryParse(systemId, out var parsed))
        {
            return (systemId, null);
        }

        var cacheKey = parsed.Kind switch
        {
            // System-id-shaped inputs canonicalise onto RawId so "abcdefg",
            // "nam:abcdefg", and "id:abcdefg" collide in the cache (they all identify the
            // same user_registry row).
            UserRegistryLookupKind.Region => parsed.RawId,
            UserRegistryLookupKind.Id => parsed.RawId,
            // Username / Discord handles keep their prefix in the cache key so a username
            // "abcdefg" doesn't spuriously alias the bare id "abcdefg".
            _ => parsed.OriginalValue,
        };

        return (cacheKey, parsed);
    }

    /// <summary>
    /// Typed replacement for magic-string column literals inside
    /// <see cref="LookupAsync"/>. Ties the <see cref="UserRegistryLookupKind"/> switch
    /// to the CQL column universe so a new lookup kind cannot accidentally drift from
    /// the SQL builder. Private/nested because its only consumer is inside this class.
    /// </summary>
    private enum UserRegistryLookupColumn
    {
        Username,
        DiscordId,
        UserId,
    }

    /// <summary>
    /// Map the <see cref="UserRegistryLookupColumn"/> enum to the on-disk CQL column name.
    /// Kept as a <c>private static</c> method rather than an extension so the enum can stay
    /// nested and inaccessible outside this class — the CQL vocabulary is an implementation
    /// detail of the registry context.
    /// </summary>
    private static string ColumnName(UserRegistryLookupColumn column) => column switch
    {
        UserRegistryLookupColumn.Username => "username",
        UserRegistryLookupColumn.DiscordId => "discord_id",
        UserRegistryLookupColumn.UserId => "user_id",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown user-registry lookup column."),
    };
}
