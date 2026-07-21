using System.Collections.Concurrent;
using Cassandra;
using System.Security.Cryptography;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaAccountRepository : IAccountRepository
{
    /// <summary>
    /// OAuth-provider identity column on the Scylla user_registry / users tables. A closed
    /// three-arm enum rather than a magic column-name string, so misspellings surface at
    /// build time and CQL interpolation flows through <see cref="ColumnName"/> as the
    /// single translation point.
    /// </summary>
    private enum ProviderColumn
    {
        Discord,
        Email,
        Apple,
    }

    /// <summary>
    /// Maps the typed provider onto the Cassandra column / lookup-table suffix. Kept
    /// switch-exhaustive with an explicit throw so a future enum member without a mapping
    /// is a hard error rather than a silent-null-column CQL statement.
    /// </summary>
    private static string ColumnName(ProviderColumn column) => column switch
    {
        ProviderColumn.Discord => "discord_id",
        ProviderColumn.Email => "email",
        ProviderColumn.Apple => "apple_id",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown provider column"),
    };

    // Reverse-map value: scoped systemId + expiry, so ResolveSystemIdByLinkTokenAsync
    // can hand back the wrapper without a string round-trip through new SystemId(...).
    private readonly record struct LinkTokenEntry(ScopedSystemId Scoped, DateTimeOffset ExpiresAt);

    private static readonly TimeSpan LinkTokenTtl = TimeSpan.FromMinutes(5);
    private readonly object _linkTokenLock = new();
    // Forward map keys on the scoped composite; reverse map keys on the typed LinkToken
    // so any accidental toString-then-dict-key path can't bypass the redacting wrapper.
    // Process-lifetime only — no storage-layer rehydration reads either dict, so the
    // key-shape choice is invisible externally.
    private readonly ConcurrentDictionary<ScopedSystemId, LinkToken> _linkTokenBySystem = new();
    private readonly ConcurrentDictionary<LinkToken, LinkTokenEntry> _systemByLinkToken = new();

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaAccountRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options
    )
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Read old username to maintain lookup table
            var oldRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT username FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var oldUsername = oldRow?.GetValue<string?>("username");

            var batch = new BatchStatement();
            if (oldRow is null)
            {
                // First touch for a JWT-scoped principal: mint the regional users row so
                // GetPublicProfileAsync (and public guarded reads that gate on it) succeed.
                // InMemory achieves the same implicitly by writing into its username map;
                // Scylla previously only issued UPDATE, leaving ShowAlter to 404 system_not_found.
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users (id, username, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()))",
                    normalizedSystemId,
                    username.Value));
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry (user_id, username, region, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                    normalizedSystemId,
                    username.Value,
                    keyspace));
            }
            else
            {
                batch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET username = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    username.Value, normalizedSystemId));
                batch.Add(new SimpleStatement(
                    $"UPDATE {ScyllaGlobalKeyspace.Name}.user_registry SET username = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                    username.Value, normalizedSystemId));
            }

            // Remove old lookup entry
            if (!string.IsNullOrWhiteSpace(oldUsername))
            {
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.users_by_username WHERE username = ?", oldUsername));
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_username WHERE username = ?", oldUsername));
            }

            // Insert new lookup entry
            if (!string.IsNullOrWhiteSpace(username))
            {
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users_by_username (username, user_id) VALUES (?, ?)",
                    username.Value, normalizedSystemId));
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry_by_username (username, user_id, region) VALUES (?, ?, ?)",
                    username.Value, normalizedSystemId, keyspace));
            }

            await session.ExecuteAsync(batch);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET description = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                description,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> UpdateAvatarAsync(SystemId systemId, AvatarUrl avatarUrl, AvatarSource source, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET avatar_url = ?, avatar_source = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                avatarUrl.Value,
                (short)source,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Null both columns together so observers can never see a half-cleared state.
            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET avatar_url = ?, avatar_source = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                null,
                null,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, cancellationToken);
    }

    public Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        // Compose is idempotent on already-scoped inputs — the safety net for routing
        // every partition-key composition through the same helper.
        var scoped = ScopedSystemId.Compose(keyspace, normalizedSystemId);
        var now = DateTimeOffset.UtcNow;

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryGetValue(scoped, out var existingToken)
                && _systemByLinkToken.TryGetValue(existingToken, out var existingEntry)
                && existingEntry.ExpiresAt > now)
            {
                return Task.FromResult(existingToken);
            }

            if (!string.IsNullOrWhiteSpace(existingToken.Value))
            {
                _linkTokenBySystem.TryRemove(scoped, out _);
                _systemByLinkToken.TryRemove(existingToken, out _);
            }

            LinkToken token = new(Guid.NewGuid().ToString());
            _linkTokenBySystem[scoped] = token;
            _systemByLinkToken[token] = new LinkTokenEntry(scoped, now.Add(LinkTokenTtl));

            return Task.FromResult(token);
        }
    }

    public Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var scoped = ScopedSystemId.Compose(keyspace, normalizedSystemId);
        var now = DateTimeOffset.UtcNow;

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryGetValue(scoped, out var token)
                && _systemByLinkToken.TryGetValue(token, out var entry)
                && entry.ExpiresAt > now)
            {
                return Task.FromResult<LinkToken?>(token);
            }

            if (!string.IsNullOrWhiteSpace(token.Value))
            {
                _linkTokenBySystem.TryRemove(scoped, out _);
                _systemByLinkToken.TryRemove(token, out _);
            }

            return Task.FromResult<LinkToken?>(null);
        }
    }

    public Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        var now = DateTimeOffset.UtcNow;
        lock (_linkTokenLock)
        {
            if (_systemByLinkToken.TryGetValue(linkToken, out var entry) && entry.ExpiresAt > now)
            {
                return Task.FromResult<SystemId?>(entry.Scoped.AsSystemId());
            }

            _systemByLinkToken.TryRemove(linkToken, out _);
            foreach (var item in _linkTokenBySystem)
            {
                // LinkToken record-struct equality is ordinal on the underlying string.
                if (item.Value == linkToken)
                {
                    _linkTokenBySystem.TryRemove(item.Key, out _);
                    break;
                }
            }

            return Task.FromResult<SystemId?>(null);
        }
    }

    public Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var scoped = ScopedSystemId.Compose(keyspace, normalizedSystemId);

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryRemove(scoped, out var token))
            {
                _systemByLinkToken.TryRemove(token, out _);
            }

            return Task.FromResult(true);
        }
    }

    // Every public IAccountRepository entry point below dispatches to an internal helper
    // with a typed ProviderColumn instead of a hand-spelled column literal. The unwrap-
    // to-.Value stays at the entry-point boundary — the internal helpers work with the
    // raw provider-value string because it flows straight into a CQL bind parameter,
    // and the bind arg position is typed `object?` where the wrapper's implicit widen
    // does not fire through boxing. DiscordId/Email/AppleId are Cat B PII wrappers with
    // no implicit widen at all, so `.Value` is the only unwrap available on this path.

    public Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
        => TryFindSystemIdByRegistryColumnAsync(ProviderColumn.Discord, discordId.Value, cancellationToken);

    // FindOrCreateSystemIdAsync and LinkIdentityToUserAsync are the consolidated OAuth-
    // login shapes. Both dispatch on ProviderIdentity into the same ProviderColumn-typed
    // internal helpers, so the pattern-match lives once here rather than being duplicated
    // across AuthController and AuthLinkController.
    public Task<SystemId?> FindOrCreateSystemIdAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => FindOrCreateSystemIdByRegistryColumnAsync(ProviderColumn.Discord, discordId.Value, cancellationToken),
            email => FindOrCreateSystemIdByRegistryColumnAsync(ProviderColumn.Email, email.Value, cancellationToken),
            appleId => FindOrCreateSystemIdByRegistryColumnAsync(ProviderColumn.Apple, appleId.Value, cancellationToken));

    public Task<AccountLinkResult> LinkIdentityToUserAsync(SystemId systemId, ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => LinkIdentityAsync(systemId, ProviderColumn.Discord, discordId.Value, cancellationToken),
            email => LinkIdentityAsync(systemId, ProviderColumn.Email, email.Value, cancellationToken),
            appleId => LinkIdentityAsync(systemId, ProviderColumn.Apple, appleId.Value, cancellationToken));

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, ProviderColumn.Discord, cancellationToken);

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, ProviderColumn.Email, cancellationToken);

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, ProviderColumn.Apple, cancellationToken);

    public async Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Fetch identity fields to clean up lookup tables
            var userRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, discord_id, email, username, apple_id, google_id FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();

            if (userRow is null)
            {
                return true;
            }
            
            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement($"DELETE FROM {keyspace}.users WHERE id = ?", normalizedSystemId));
            deleteBatch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE user_id = ?", normalizedSystemId));

            // Clean up denormalized identity lookup tables
            var identityColumns = new[] { "discord_id", "email", "username", "apple_id", "google_id" };
            foreach (var col in identityColumns)
            {
                var value = userRow.GetValue<string?>(col);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    deleteBatch.Add(new SimpleStatement($"DELETE FROM {keyspace}.users_by_{col} WHERE {col} = ?", value));
                    deleteBatch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_{col} WHERE {col} = ?", value));
                }
            }

            await session.ExecuteAsync(deleteBatch);

            return true;
        }, cancellationToken);
    }

    public async Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            var profileQuery = new SimpleStatement(
                $"SELECT username, avatar_url, avatar_source, description, discord_id, email, apple_id FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var profile = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();
            if (profile is null)
            {
                return null;
            }

            return new AccountPublicProfileReadModel(
                new(normalizedSystemId),
                profile.GetValue<string?>("username") is { } username ? new Username(username) : null,
                profile.GetValue<string?>("description"),
                AvatarUrl.FromNullable(profile.GetValue<string?>("avatar_url")),
                profile.GetValue<short?>("avatar_source").FromCodeOrNull<AvatarSource>(),
                profile.GetValue<string?>("discord_id") is { } discordId ? new DiscordId(discordId) : null,
                profile.GetValue<string?>("email") is { } email ? new Email(email) : null,
                profile.GetValue<string?>("apple_id") is { } appleId ? new AppleId(appleId) : null);
        }, cancellationToken);
    }

    public async Task<PublicSystemReadModel?> GetPublicSystemAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;

            // Narrower SELECT than GetPublicProfileAsync — no discord/email/apple columns,
            // since the public wire projection intentionally drops them.
            var profileQuery = new SimpleStatement(
                $"SELECT username, avatar_url, avatar_source, description FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var profile = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();
            if (profile is null)
            {
                return null;
            }

            return new PublicSystemReadModel(
                Id: new SystemId(normalizedSystemId),
                AvatarUrl: AvatarUrl.FromNullable(profile.GetValue<string?>("avatar_url")),
                AvatarSource: profile.GetValue<short?>("avatar_source").FromCodeOrNull<AvatarSource>(),
                Username: profile.GetValue<string?>("username") is { } username ? new Username(username) : null,
                Description: profile.GetValue<string?>("description"));
        }, cancellationToken);
    }

    // Returns the scoped `{region}:{userId}` composite wrapped in a SystemId — in-process
    // caches and downstream callers all operate in the scoped-composite shape. The column
    // parameter is a typed ProviderColumn enum, with the CQL literal derived locally via
    // ColumnName(...); a misspelled column would be a build failure rather than a silent
    // runtime "table does not exist".
    private async Task<SystemId?> TryFindSystemIdByRegistryColumnAsync(ProviderColumn column, string value, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<SystemId?>(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var columnName = ColumnName(column);
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var query = new SimpleStatement(
                $"SELECT user_id, region FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} WHERE {columnName} = ? LIMIT 1",
                value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is not null)
            {
                var userId = NormalizeRegistryUserId(new(row.GetValue<string>("user_id")));
                var region = row.GetValue<string?>("region") ?? _keyspaceResolver.DefaultKeyspace;
                return ScopedSystemId.Compose(region, userId).AsSystemId();
            }

            return null;
        }, _options, cancellationToken);
    }

    private async Task<SystemId?> FindOrCreateSystemIdByRegistryColumnAsync(ProviderColumn column, string value, CancellationToken cancellationToken)
    {
        var existing = await TryFindSystemIdByRegistryColumnAsync(column, value, cancellationToken);
        if (existing is { } typedExisting && !string.IsNullOrWhiteSpace(typedExisting))
        {
            return typedExisting;
        }

        return await DatabaseTransientRetry.ExecuteScyllaAsync<SystemId?>(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var columnName = ColumnName(column);
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            const string idChars = "abcdefghijklmnopqrstuvwxyz";

            // User doesn't exist; auto-create new account
            var newRegion = _keyspaceResolver.DefaultKeyspace; //TODO: Maybe look into geoip-based region resolution here instead of just defaulting?
            var newUserId = Random.Shared.GetString(idChars, 7);
            var keyspace = newRegion; // ResolveRegionalKeyspace would just return the newRegion

            // Write regional user + global registry together to reduce split-write orphans.
            var createUserBatch = new BatchStatement()
                .Add(new SimpleStatement(
                    // Four columns -> four bind targets: two prepared parameters plus two literal
                    // toTimestamp(now()) calls. Adding a third placeholder before the literals (the
                    // shape this method had previously) tipped the value count over the column
                    // count and Cassandra rejected the batch with "Unmatched column names/values".
                    $"INSERT INTO {keyspace}.users (id, {columnName}, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value
                ))
                .Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry (user_id, {columnName}, region, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value,
                    newRegion
                ))
                // Maintain denormalized lookup tables
                .Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users_by_{columnName} ({columnName}, user_id) VALUES (?, ?)",
                    value,
                    newUserId
                ))
                .Add(new SimpleStatement(
                    $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} ({columnName}, user_id, region) VALUES (?, ?, ?)",
                    value,
                    newUserId,
                    newRegion
                ));

            await session.ExecuteAsync(createUserBatch);

            return ScopedSystemId.Compose(newRegion, newUserId).AsSystemId();
        }, _options, cancellationToken);
    }

    // Fixed-point loop because ScopedSystemId.StripRegionPrefix strips at most one
    // leading region tag per call; a legacy row that somehow carried a double-prefixed value
    // ("nam:nam:abcdefg") would otherwise slip through with the outer prefix intact.
    private string NormalizeRegistryUserId(SystemId userId)
    {
        var normalized = _keyspaceResolver.NormalizeSystemId(userId);
        for (var i = 0; i < 2; i++)
        {
            var next = _keyspaceResolver.NormalizeSystemId(new(normalized));
            if (next == normalized)
            {
                break;
            }

            normalized = next;
        }

        return normalized;
    }

    private async Task<AccountLinkResult> LinkIdentityAsync(SystemId systemId, ProviderColumn column, string value, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return AccountLinkResult.UserNotFound;
            }

            var columnName = ColumnName(column);
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var owner = await TryFindSystemIdByRegistryColumnAsync(column, value, cancellationToken);
            if (owner is { } typedOwner && !string.IsNullOrWhiteSpace(typedOwner))
            {
                var normalizedOwner = NormalizeRegistryUserId(typedOwner);
                if (normalizedOwner != normalizedSystemId)
                {
                    return AccountLinkResult.UserExists;
                }
            }

            var userRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, {columnName} FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();

            if (userRow is null)
            {
                return AccountLinkResult.UserNotFound;
            }

            var alreadyLinked = userRow.GetValue<string?>(columnName);
            if (!string.IsNullOrWhiteSpace(alreadyLinked))
            {
                return AccountLinkResult.AlreadyLinked;
            }

            var linkBatch = new BatchStatement();
            linkBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                value,
                normalizedSystemId));
            linkBatch.Add(new SimpleStatement(
                $"UPDATE {ScyllaGlobalKeyspace.Name}.user_registry SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                value,
                normalizedSystemId));
            // Maintain denormalized lookup tables
            linkBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.users_by_{columnName} ({columnName}, user_id) VALUES (?, ?)",
                value,
                normalizedSystemId));
            linkBatch.Add(new SimpleStatement(
                $"INSERT INTO {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} ({columnName}, user_id, region) VALUES (?, ?, ?)",
                value,
                normalizedSystemId,
                keyspace));
            await session.ExecuteAsync(linkBatch);

            return AccountLinkResult.Success;
        }, _options, cancellationToken);
    }

    private async Task<bool> UnlinkIdentityAsync(SystemId systemId, ProviderColumn column, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var columnName = ColumnName(column);
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Read old value to delete from lookup tables
            var oldRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT {columnName} FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var oldValue = oldRow?.GetValue<string?>(columnName);

            var unlinkBatch = new BatchStatement();
            unlinkBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                null,
                normalizedSystemId));
            unlinkBatch.Add(new SimpleStatement(
                $"UPDATE {ScyllaGlobalKeyspace.Name}.user_registry SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                null,
                normalizedSystemId));

            // Remove from denormalized lookup tables if old value existed
            if (!string.IsNullOrWhiteSpace(oldValue))
            {
                unlinkBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.users_by_{columnName} WHERE {columnName} = ?",
                    oldValue));
                unlinkBatch.Add(new SimpleStatement(
                    $"DELETE FROM {ScyllaGlobalKeyspace.Name}.user_registry_by_{columnName} WHERE {columnName} = ?",
                    oldValue));
            }

            await session.ExecuteAsync(unlinkBatch);
            return true;
        }, _options, cancellationToken);
    }

}
