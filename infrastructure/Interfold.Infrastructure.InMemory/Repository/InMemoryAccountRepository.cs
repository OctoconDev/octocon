using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryAccountRepository : IAccountRepository
{
    // Matches the Scylla port's 5-minute link-token expiry.
    private static readonly TimeSpan LinkTokenTtl = TimeSpan.FromMinutes(5);

    private readonly record struct LinkTokenEntry(ScopedSystemId Scoped, DateTimeOffset ExpiresAt);

    // Identity-side reverse dicts key on raw string to preserve
    // StringComparer.OrdinalIgnoreCase (needed for email); the wrappers are ordinal-strict.
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

        // Deterministic derivation: same system → same token (integration-test contract).
        // Wrapping inside GetOrAdd keeps the derived hash off the stack as a bare string.
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

        // Honour TTL so "get" never returns a token Resolve would then refuse.
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

        // On miss/expired, scrub both sides — deterministic derivation would re-adopt
        // a dangling pointer.
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

    // ProviderIdentity dispatch → shared FindOrCreate/Link/Unlink helpers below.
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

        // Presence check must agree with GetPublicProfileAsync — otherwise
        // PublicSystemsController.Show 404s rows SystemMustExistAttribute lets through.
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

    // Drop a link-token from both maps. Lazy scrub guards against dangling reverse-map
    // pointers that deterministic-hash tokens would otherwise re-adopt.
    private void ScrubLinkToken(ScopedSystemId? systemKey = null, LinkToken? linkTokenValue = null)
    {
        if (linkTokenValue is { } token)
        {
            _systemByLinkToken.TryRemove(token, out _);
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

    // Idempotent success matches the Scylla adapter — "nothing to unlink" is not an error.
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

    // Scoped param pins the "caller resolved to scoped composite" invariant at the type level.
    private void EnsureEncryptionSaltForSystem(ScopedSystemId scoped)
    {
        if (_encryptionStates is null)
            return;

        // EncryptionSalt.NewRandom() keeps the base64 salt off the stack as a bare string.
        _ = _encryptionStates.UpsertAsync(scoped.AsSystemId(), false, null, EncryptionSalt.NewRandom(), CancellationToken.None);
    }
}

