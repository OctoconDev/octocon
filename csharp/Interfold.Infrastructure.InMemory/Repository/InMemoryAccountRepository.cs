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
        var systemKey = GetSystemKey(systemId);
        _usernameBySystem[systemKey] = username;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateDescriptionAsync(SystemId systemId, string description, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _descriptionBySystem[systemKey] = description;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateAvatarAsync(SystemId systemId, AvatarUrl avatarUrl, AvatarSource source, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _avatarBySystem[systemKey] = avatarUrl;
        _avatarSourceBySystem[systemKey] = source;
        return Task.FromResult(true);
    }

    public Task<bool> ClearAvatarAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        _avatarBySystem.TryRemove(systemKey, out _);
        _avatarSourceBySystem.TryRemove(systemKey, out _);
        return Task.FromResult(true);
    }

    public Task<LinkToken> GetOrCreateLinkTokenAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
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
        var systemKey = GetSystemKey(systemId);
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
        var systemKey = GetSystemKey(systemId);
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

    // Two ProviderIdentity-keyed entry points dispatch to per-branch private helpers.
    // The three FindOrCreate* helpers each write to a distinct pair of dictionaries
    // (discord / email / apple); LinkIdentifier is generic across the identity wrapper.
    public Task<SystemId?> FindOrCreateSystemIdAsync(ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity switch
        {
            { Discord: { } discordId } => FindOrCreateSystemIdByDiscord(discordId),
            { Google: { } email } => FindOrCreateSystemIdByEmail(email),
            { Apple: { } appleId } => FindOrCreateSystemIdByApple(appleId),
            _ => Task.FromResult<SystemId?>(null),
        };

    public Task<AccountLinkResult> LinkIdentityToUserAsync(SystemId systemId, ProviderIdentity identity, CancellationToken cancellationToken = default)
        => identity switch
        {
            { Discord: { } discordId } => Task.FromResult(LinkIdentifier(systemId, discordId, _discordBySystem, _systemByDiscord, static id => id.Value)),
            { Google: { } email } => Task.FromResult(LinkIdentifier(systemId, email, _emailBySystem, _systemByEmail, static e => e.Value)),
            { Apple: { } appleId } => Task.FromResult(LinkIdentifier(systemId, appleId, _appleBySystem, _systemByApple, static id => id.Value)),
            _ => Task.FromResult(AccountLinkResult.UserNotFound),
        };

    private Task<SystemId?> FindOrCreateSystemIdByDiscord(DiscordId discordId)
    {
        if (string.IsNullOrWhiteSpace(discordId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByDiscord.TryGetValue(discordId.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(scopedSystemId.AsSystemId());
        }

        // Hold the ScopedSystemId typed locally so EnsureEncryptionSaltForSystem receives
        // the wrapper directly and the return widens through AsSystemId with no string
        // round-trip. Reverse-map dicts key on ScopedSystemId so the write key matches
        // the SCOPED composite every downstream read (LinkIdentifier / Unlink* /
        // GetPublicProfile) uses via GetSystemKey.
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new(newSystemId)), newSystemId);
        _discordBySystem[scopedNew] = discordId;
        _systemByDiscord[discordId.Value] = scopedNew;

        EnsureEncryptionSaltForSystem(scopedNew);
        return Task.FromResult<SystemId?>(scopedNew.AsSystemId());
    }

    private Task<SystemId?> FindOrCreateSystemIdByEmail(Email email)
    {
        if (string.IsNullOrWhiteSpace(email.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByEmail.TryGetValue(email.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(scopedSystemId.AsSystemId());
        }

        // See FindOrCreateSystemIdByDiscord above for the "typed local + scoped-composite
        // reverse-map key" rationale.
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new(newSystemId)), newSystemId);
        _emailBySystem[scopedNew] = email;
        _systemByEmail[email.Value] = scopedNew;

        EnsureEncryptionSaltForSystem(scopedNew);
        return Task.FromResult<SystemId?>(scopedNew.AsSystemId());
    }

    private Task<SystemId?> FindOrCreateSystemIdByApple(AppleId appleId)
    {
        if (string.IsNullOrWhiteSpace(appleId.Value))
        {
            return Task.FromResult<SystemId?>(null);
        }

        if (_systemByApple.TryGetValue(appleId.Value, out var scopedSystemId))
        {
            return Task.FromResult<SystemId?>(scopedSystemId.AsSystemId());
        }

        // See FindOrCreateSystemIdByDiscord above for the "typed local + scoped-composite
        // reverse-map key" rationale.
        var newSystemId = Guid.NewGuid().ToString("N");
        var scopedNew = ScopedSystemId.Compose(_regionContext.ResolveUserRegion(new(newSystemId)), newSystemId);
        _appleBySystem[scopedNew] = appleId;
        _systemByApple[appleId.Value] = scopedNew;

        EnsureEncryptionSaltForSystem(scopedNew);
        return Task.FromResult<SystemId?>(scopedNew.AsSystemId());
    }

    public Task<bool> UnlinkDiscordAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId.Value))
        {
            _systemByDiscord.TryRemove(discordId.Value, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkEmailAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email.Value))
        {
            _systemByEmail.TryRemove(email.Value, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkAppleAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId.Value))
        {
            _systemByApple.TryRemove(appleId.Value, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        _usernameBySystem.TryRemove(systemKey, out _);
        _descriptionBySystem.TryRemove(systemKey, out _);
        _avatarBySystem.TryRemove(systemKey, out _);
        _avatarSourceBySystem.TryRemove(systemKey, out _);
        if (_linkTokenBySystem.TryRemove(systemKey, out var token))
        {
            _systemByLinkToken.TryRemove(token, out _);
        }

        if (_discordBySystem.TryRemove(systemKey, out var discordId) && !string.IsNullOrWhiteSpace(discordId.Value))
        {
            _systemByDiscord.TryRemove(discordId.Value, out _);
        }

        if (_emailBySystem.TryRemove(systemKey, out var email) && !string.IsNullOrWhiteSpace(email.Value))
        {
            _systemByEmail.TryRemove(email.Value, out _);
        }

        if (_appleBySystem.TryRemove(systemKey, out var appleId) && !string.IsNullOrWhiteSpace(appleId.Value))
        {
            _systemByApple.TryRemove(appleId.Value, out _);
        }

        return Task.FromResult(true);
    }

    public Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
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

    private ScopedSystemId GetSystemKey(SystemId systemId) => InMemoryStorageKeys.ForSystem(_regionContext, systemId);

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

        var systemKey = GetSystemKey(systemId);
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
