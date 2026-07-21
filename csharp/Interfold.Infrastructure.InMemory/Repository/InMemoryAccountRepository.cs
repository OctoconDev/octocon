using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryAccountRepository : IAccountRepository
{
    /// <summary>
    /// TTL for link tokens — matches the 5-minute expiry the Scylla port enforces so both
    /// adapters have the same "get" contract.
    /// </summary>
    private static readonly TimeSpan LinkTokenTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reverse map value: scoped systemId + expiry, so
    /// <see cref="ResolveSystemIdByLinkTokenAsync"/> honours the TTL and stale entries can
    /// be scrubbed lazily on the first read that sees them expired.
    /// </summary>
    private readonly record struct LinkTokenEntry(ScopedSystemId Scoped, DateTimeOffset ExpiresAt);

    // Per-system dicts key on ScopedSystemId (record-struct with ordinal equality on
    // Value). The identity-side dicts (_systemBy{Discord,Email,Apple}) key on raw string
    // via StringComparer.OrdinalIgnoreCase — the identity wrappers themselves are
    // ordinal-strict and would lose the case-insensitive lookup contract for email if
    // used directly as the key.
    private readonly ConcurrentDictionary<ScopedSystemId, Username> _usernameBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, string> _descriptionBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, AvatarUrl> _avatarBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, AvatarSource> _avatarSourceBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, LinkToken> _linkTokenBySystem = new();
    private readonly ConcurrentDictionary<LinkToken, LinkTokenEntry> _systemByLinkToken = new();
    private readonly ConcurrentDictionary<ScopedSystemId, DiscordId> _discordBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, Email> _emailBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, AppleId> _appleBySystem = new();
    private readonly ConcurrentDictionary<string, ScopedSystemId> _systemByDiscord = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ScopedSystemId> _systemByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ScopedSystemId> _systemByApple = new(StringComparer.OrdinalIgnoreCase);

    private readonly IEncryptionStateRepository? _encryptionStates;
    private readonly IRegionContext _regionContext;
    // Injectable clock so unit tests can exercise the TTL branch without a 5-minute wall
    // wait. Defaults to TimeProvider.System — no scheduled work runs on the repository.
    private readonly TimeProvider _timeProvider;

    public InMemoryAccountRepository(
        IRegionContext regionContext,
        IEncryptionStateRepository? encryptionStates = null,
        TimeProvider? timeProvider = null)
    {
        _regionContext = regionContext;
        _encryptionStates = encryptionStates;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<bool> UpdateUsernameAsync(SystemId systemId, Username username, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        _usernameBySystem[systemKey] = username;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        _descriptionBySystem[systemKey] = description;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateAvatarAsync(SystemId systemId, AvatarUrl avatarUrl, AvatarSource source, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        _avatarBySystem[systemKey] = avatarUrl;
        _avatarSourceBySystem[systemKey] = source;
        return Task.FromResult(true);
    }

    public Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        _avatarBySystem.TryRemove(systemKey, out _);
        _avatarSourceBySystem.TryRemove(systemKey, out _);
        return Task.FromResult(true);
    }

    public Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var scoped = ResolveScoped(systemId);
        var now = _timeProvider.GetUtcNow();

        // Deterministic token derivation: "same system → same token" is a contract the
        // integration tests lean on. Every call refreshes the expiry so a live client
        // that keeps calling get-or-create doesn't spuriously expire. The derived hash
        // is wrapped as LinkToken inside the GetOrAdd factory so the token spends zero
        // time as a bare string local — every downstream reference goes through the
        // redacting wrapper.
        var token = _linkTokenBySystem.GetOrAdd(systemKey, static key =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
            return new(Convert.ToHexString(hash)[..32].ToLowerInvariant());
        });

        _systemByLinkToken[token] = new LinkTokenEntry(scoped, now.Add(LinkTokenTtl));

        return Task.FromResult(token);
    }

    public Task<LinkToken?> GetLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (!_linkTokenBySystem.TryGetValue(systemKey, out var token))
        {
            return Task.FromResult<LinkToken?>(null);
        }

        // Honour the TTL on the read path so a client that only calls "get" never sees a
        // token that ResolveSystemIdByLinkTokenAsync would then refuse.
        if (_systemByLinkToken.TryGetValue(token, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return Task.FromResult<LinkToken?>(token);
        }

        ScrubLinkToken(systemKey, token);
        return Task.FromResult<LinkToken?>(null);
    }

    public Task<SystemId?> ResolveSystemIdByLinkTokenAsync(LinkToken linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        // Check TTL on lookup; on miss (nonexistent or expired) scrub BOTH sides so the
        // deterministic-token derivation doesn't leave a dangling pointer that a later
        // GetOrCreate would silently re-adopt.
        if (_systemByLinkToken.TryGetValue(linkToken, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return Task.FromResult<SystemId?>(entry.Scoped.AsSystemId());
        }

        ScrubLinkToken(linkTokenValue: linkToken);
        return Task.FromResult<SystemId?>(null);
    }

    public Task<bool> ClearLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (_linkTokenBySystem.TryRemove(systemKey, out var token))
        {
            _systemByLinkToken.TryRemove(token, out _);
        }

        return Task.FromResult(true);
    }

    public Task<SystemId?> TryFindSystemIdByDiscordIdAsync(DiscordId discordId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discordId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        return Task.FromResult<SystemId?>(
            _systemByDiscord.TryGetValue(discordId.Value, out var scopedSystemId)
                ? scopedSystemId.AsSystemId()
                : null);
    }

    // Every ProviderIdentity-keyed public entry point below dispatches to the same three
    // generic helpers (FindOrCreateIdentifier / LinkIdentifier / UnlinkIdentifier) with
    // per-branch dictionary + raw-value-extractor lambdas. Keeping the dispatch here and
    // the storage-agnostic body in the generic means adding a fourth provider takes only a
    // new dict pair + a fourth MatchOrThrow arm — not a fresh copy of the three-way body.
    public Task<SystemId?> FindOrCreateSystemIdAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => FindOrCreateIdentifier(discordId, _discordBySystem, _systemByDiscord, static id => id.Value),
            email => FindOrCreateIdentifier(email, _emailBySystem, _systemByEmail, static e => e.Value),
            appleId => FindOrCreateIdentifier(appleId, _appleBySystem, _systemByApple, static id => id.Value));

    public Task<AccountLinkResult> LinkIdentityToUserAsync(SystemId systemId, ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity.MatchOrThrow(
            discordId => Task.FromResult(LinkIdentifier(systemId, discordId, _discordBySystem, _systemByDiscord, static id => id.Value)),
            email => Task.FromResult(LinkIdentifier(systemId, email, _emailBySystem, _systemByEmail, static e => e.Value)),
            appleId => Task.FromResult(LinkIdentifier(systemId, appleId, _appleBySystem, _systemByApple, static id => id.Value)));

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => Task.FromResult(UnlinkIdentifier(systemId, _discordBySystem, _systemByDiscord, static id => id.Value));

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => Task.FromResult(UnlinkIdentifier(systemId, _emailBySystem, _systemByEmail, static e => e.Value));

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
        => Task.FromResult(UnlinkIdentifier(systemId, _appleBySystem, _systemByApple, static id => id.Value));

    public Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        _usernameBySystem.TryRemove(systemKey, out _);
        _descriptionBySystem.TryRemove(systemKey, out _);
        _avatarBySystem.TryRemove(systemKey, out _);
        _avatarSourceBySystem.TryRemove(systemKey, out _);
        if (_linkTokenBySystem.TryRemove(systemKey, out var token))
        {
            _systemByLinkToken.TryRemove(token, out _);
        }

        // Delete = the union of every Unlink* — same forward/reverse dict pattern, so route
        // through the shared helper rather than open-code the three identical blocks.
        UnlinkIdentifier(systemId, _discordBySystem, _systemByDiscord, static id => id.Value);
        UnlinkIdentifier(systemId, _emailBySystem, _systemByEmail, static e => e.Value);
        UnlinkIdentifier(systemId, _appleBySystem, _systemByApple, static id => id.Value);

        return Task.FromResult(true);
    }

    public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        Username? username = _usernameBySystem.TryGetValue(systemKey, out var u) ? u : null;
        var description = _descriptionBySystem.TryGetValue(systemKey, out var d) ? d : null;
        AvatarUrl? avatarUrl = _avatarBySystem.TryGetValue(systemKey, out var a) ? a : null;
        AvatarSource? avatarSource = _avatarSourceBySystem.TryGetValue(systemKey, out var s) ? s : null;
        DiscordId? discordId = _discordBySystem.TryGetValue(systemKey, out var discord) ? discord : null;
        Email? email = _emailBySystem.TryGetValue(systemKey, out var e) ? e : null;
        AppleId? appleId = _appleBySystem.TryGetValue(systemKey, out var apple) ? apple : null;

        if (username is null && description is null && avatarUrl is null && discordId is null && email is null && appleId is null)
        {
            return Task.FromResult<AccountPublicProfileReadModel?>(null);
        }

        return Task.FromResult<AccountPublicProfileReadModel?>(
            new AccountPublicProfileReadModel(
                systemId,
                username,
                description,
                avatarUrl,
                avatarSource,
                discordId,
                email,
                appleId));
    }

    public Task<PublicSystemReadModel?> GetPublicSystemAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        Username? username = _usernameBySystem.TryGetValue(systemKey, out var u) ? u : null;
        var description = _descriptionBySystem.TryGetValue(systemKey, out var d) ? d : null;
        AvatarUrl? avatarUrl = _avatarBySystem.TryGetValue(systemKey, out var a) ? a : null;
        AvatarSource? avatarSource = _avatarSourceBySystem.TryGetValue(systemKey, out var s) ? s : null;

        // Same existence check as GetPublicProfileAsync — an account is considered
        // "present" when any of the identity-bearing fields are set. Falling back on the
        // internal Discord/Email/Apple pointers keeps the two projections in agreement:
        // a system that returns non-null from GetPublicProfileAsync must also return
        // non-null here, otherwise PublicSystemsController.Show would 404 rows that
        // SystemMustExistAttribute happily lets through.
        var hasIdentity = username is not null
            || description is not null
            || avatarUrl is not null
            || _discordBySystem.ContainsKey(systemKey)
            || _emailBySystem.ContainsKey(systemKey)
            || _appleBySystem.ContainsKey(systemKey);

        if (!hasIdentity)
        {
            return Task.FromResult<PublicSystemReadModel?>(null);
        }

        return Task.FromResult<PublicSystemReadModel?>(
            new PublicSystemReadModel(
                Id: systemId,
                AvatarUrl: avatarUrl,
                AvatarSource: avatarSource,
                Username: username,
                Description: description));
    }

    private ScopedSystemId ResolveScoped(SystemId systemId)
        => ScopedSystemId.Compose(_regionContext.ResolveUserRegion(systemId), systemId);

    /// <summary>
    /// Drop a link-token from both maps. Used by the two read paths that discover a stale
    /// or missing entry — the deterministic-token hash means a re-issued token can't bury
    /// a stale mapping, so lazy scrub on read is the guardrail against dangling
    /// reverse-map pointers.
    /// </summary>
    private void ScrubLinkToken(ScopedSystemId? systemKey = null, LinkToken? linkTokenValue = null)
    {
        if (linkTokenValue is { } token)
        {
            _systemByLinkToken.TryRemove(token, out _);
            // Also drop the systemKey → token pointer if it still references this token
            // (deterministic-hash tokens make this cheap; no scan of the entire dictionary).
            foreach (var kvp in _linkTokenBySystem)
            {
                if (kvp.Value == token)
                {
                    _linkTokenBySystem.TryRemove(kvp.Key, out _);
                    break;
                }
            }
        }

        if (systemKey is { } key && _linkTokenBySystem.TryRemove(key, out var storedToken))
        {
            _systemByLinkToken.TryRemove(storedToken, out _);
        }
    }

    /// <summary>
    /// Generic-typed find-or-create helper. <typeparamref name="TIdentity"/> is one of
    /// <see cref="DiscordId"/>, <see cref="Email"/>, <see cref="AppleId"/>. Existing user:
    /// unwrapped from the reverse map via <paramref name="extractRawValue"/> (raw string
    /// key so <see cref="StringComparer.OrdinalIgnoreCase"/> semantics survive for email);
    /// missing user: mint a fresh systemId + ScopedSystemId, write both dicts, seed the
    /// encryption salt. Kept typed locally so <see cref="EnsureEncryptionSaltForSystem"/>
    /// receives the wrapper directly and the return widens through <c>AsSystemId</c>
    /// without a string round-trip.
    /// </summary>
    private Task<SystemId?> FindOrCreateIdentifier<TIdentity>(
        TIdentity identifier,
        ConcurrentDictionary<ScopedSystemId, TIdentity> identifierBySystem,
        ConcurrentDictionary<string, ScopedSystemId> systemByIdentifier,
        Func<TIdentity, string> extractRawValue)
        where TIdentity : struct
    {
        var rawValue = extractRawValue(identifier);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (systemByIdentifier.TryGetValue(rawValue, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(scopedSystemId.AsSystemId());
        }

        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new(newSystemId)), newSystemId);
        identifierBySystem[scopedNew] = identifier;
        systemByIdentifier[rawValue] = scopedNew;

        EnsureEncryptionSaltForSystem(scopedNew);
        return Task.FromResult<SystemId?>(scopedNew.AsSystemId());
    }

    /// <summary>
    /// Generic-typed unlink helper. Drops the (systemKey -&gt; identifier) forward pointer
    /// and, if the removed identifier carried a non-empty raw value, the paired
    /// (raw -&gt; systemKey) reverse pointer. Always returns true — parity with the Scylla
    /// adapter, which treats "user has nothing to unlink" as an idempotent success.
    /// </summary>
    private bool UnlinkIdentifier<TIdentity>(
        SystemId systemId,
        ConcurrentDictionary<ScopedSystemId, TIdentity> identifierBySystem,
        ConcurrentDictionary<string, ScopedSystemId> systemByIdentifier,
        Func<TIdentity, string> extractRawValue)
        where TIdentity : struct
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (identifierBySystem.TryRemove(systemKey, out var identifier))
        {
            var rawValue = extractRawValue(identifier);
            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                systemByIdentifier.TryRemove(rawValue, out _);
            }
        }

        return true;
    }

    /// <summary>
    /// Generic-typed link helper. <typeparamref name="TIdentity"/> is one of
    /// <see cref="DiscordId"/>, <see cref="Email"/>, <see cref="AppleId"/>; the
    /// <paramref name="extractRawValue"/> accessor pulls the underlying string only where
    /// it's needed for the reverse-map key (which stays string-typed to keep the
    /// case-insensitive <see cref="StringComparer.OrdinalIgnoreCase"/> semantics for
    /// email-and-friends). The three call-sites feed static lambdas so there is no
    /// allocation per call.
    /// </summary>
    private AccountLinkResult LinkIdentifier<TIdentity>(
        SystemId systemId,
        TIdentity identifier,
        ConcurrentDictionary<ScopedSystemId, TIdentity> identifierBySystem,
        ConcurrentDictionary<string, ScopedSystemId> systemByIdentifier,
        Func<TIdentity, string> extractRawValue)
        where TIdentity : struct
    {
        var rawValue = extractRawValue(identifier);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return AccountLinkResult.UserNotFound;
        }

        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var scopedSystemId = ResolveScoped(systemId);

        if (_usernameBySystem.ContainsKey(systemKey) is false &&
            _descriptionBySystem.ContainsKey(systemKey) is false &&
            _avatarBySystem.ContainsKey(systemKey) is false &&
            _linkTokenBySystem.ContainsKey(systemKey) is false)
        {
            return AccountLinkResult.UserNotFound;
        }

        if (identifierBySystem.TryGetValue(systemKey, out var existing)
            && !string.IsNullOrWhiteSpace(extractRawValue(existing)))
        {
            return AccountLinkResult.AlreadyLinked;
        }

        if (systemByIdentifier.TryGetValue(rawValue, out var owner) && owner != scopedSystemId)
        {
            return AccountLinkResult.UserExists;
        }

        identifierBySystem[systemKey] = identifier;
        systemByIdentifier[rawValue] = scopedSystemId;
        return AccountLinkResult.Success;
    }

    /// <summary>
    /// Seed a per-system encryption salt at first-touch by the FindOrCreate paths. Takes
    /// <see cref="ScopedSystemId"/> so the "caller already resolved this to a scoped
    /// composite" invariant is pinned at the type level.
    /// </summary>
    private void EnsureEncryptionSaltForSystem(ScopedSystemId scoped)
    {
        if (_encryptionStates is null)
            return;

        // Mint via EncryptionSalt.NewRandom() so the raw base64 salt spends zero time as
        // a bare local (ToString() on EncryptionSalt redacts; on a string it would not).
        _ = _encryptionStates.UpsertAsync(scoped.AsSystemId(), false, null, EncryptionSalt.NewRandom(), CancellationToken.None);
    }
}

