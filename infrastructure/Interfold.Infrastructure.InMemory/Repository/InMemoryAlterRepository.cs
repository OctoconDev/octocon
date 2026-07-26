using System.Collections.Concurrent;
using System.Diagnostics;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Alters;
using Interfold.Shared.Domain.Observability;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryAlterRepository : IAlterRepository
{
    private sealed class AlterState
    {
        public required AlterId AlterId { get; init; }
        public string? Alias { get; set; }
        public AvatarUrl? AvatarUrl { get; set; }
        public AvatarSource? AvatarSource { get; set; }
        public string? Description { get; set; }
        public HexColor? Color { get; set; }
        public string? Pronouns { get; set; }
        public string? ProxyName { get; set; }
        public string Name { get; set; } = string.Empty;
        // Matches Scylla insert semantics: alters are Private until owner opens visibility.
        public VisibilityLevel VisibilityLevel { get; set; } = VisibilityLevel.Private;
        public Dictionary<FieldId, string?> Fields { get; } = new();
        public bool Untracked { get; set; }
        public bool Archived { get; set; }
        public bool Pinned { get; set; }
        public List<string> DiscordProxies { get; } = new();
        public DateTime InsertedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<AlterId, AlterState>> _bySystem = new();
    // checked((short)…) on increment traps overflow instead of handing back an already-used id.
    private readonly ConcurrentDictionary<ScopedSystemId, short> _nextIdBySystem = new();
    private readonly IFriendshipRepository? _friendships;
    private readonly ISettingsFieldRepository? _settingsFields;
    private readonly IPollRepository? _polls;
    private readonly ILogger<InMemoryAlterRepository> _logger;

    public InMemoryAlterRepository(
        IRegionContext regionContext,
        IFriendshipRepository friendships,
        ISettingsFieldRepository settingsFields,
        IPollRepository polls,
        ILogger<InMemoryAlterRepository> logger)
    {
        _regionContext = regionContext;
        _friendships = friendships;
        _settingsFields = settingsFields;
        _polls = polls;
        _logger = logger;
    }

    public Task<AlterId?> CreateAsync(
        SystemId systemId,
        CreateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<AlterId, AlterState>());
        AlterId next = new(_nextIdBySystem.AddOrUpdate(systemKey, (short)1, (_, current) => checked((short)(current + 1))));

        var now = command.CreatedAt.UtcDateTime;
        var created = store.TryAdd(next, new AlterState
        {
            AlterId = next,
            Name = command.Name,
            InsertedAt = now,
            UpdatedAt = now,
        });

        return Task.FromResult<AlterId?>(created ? next : null);
    }

    public Task<bool> ExistsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        var exists = TryGetStore(systemId, out var store) && store.ContainsKey(alterId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(
        SystemId systemId,
        UpdateAlterCommand command,
        CancellationToken cancellationToken = default
    )
    {
        if (!TryGetAlter(systemId, command.AlterId, out var existing))
        {
            return Task.FromResult(false);
        }

        if (!string.IsNullOrWhiteSpace(command.Alias))
        {
            existing.Alias = command.Alias;
        }

        if (!string.IsNullOrWhiteSpace(command.Name))
        {
            existing.Name = command.Name;
        }

        if (command.Description is not null)
        {
            existing.Description = command.Description;
        }

        if (command.Color is not null)
        {
            existing.Color = command.Color;
        }

        if (command.Pronouns is not null)
        {
            existing.Pronouns = command.Pronouns;
        }

        if (command.ProxyName is not null)
        {
            existing.ProxyName = command.ProxyName;
        }

        if (command.SecurityLevel is not null)
        {
            existing.VisibilityLevel = command.SecurityLevel.Value;
        }

        if (command.ClearAvatar)
        {
            existing.AvatarUrl = null;
            existing.AvatarSource = null;
        }
        else if (command.AvatarUrl is not null)
        {
            // Domain handler rejects half-set; treat AvatarSource as required here.
            existing.AvatarUrl = command.AvatarUrl;
            existing.AvatarSource = command.AvatarSource ?? AvatarSource.Local;
        }

        if (command.Fields is not null)
        {
            foreach (var field in command.Fields)
            {
                existing.Fields[field.Id] = field.Value;
            }
        }

        if (command.Untracked is not null)
        {
            existing.Untracked = command.Untracked.Value;
        }

        if (command.Archived is not null)
        {
            existing.Archived = command.Archived.Value;
        }

        if (command.Pinned is not null)
        {
            existing.Pinned = command.Pinned.Value;
        }

        existing.UpdatedAt = command.UpdatedAt.UtcDateTime;
        return Task.FromResult(true);
    }

    public async Task<bool> DeleteAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        if (!TryGetStore(systemId, out var store))
        {
            return false;
        }

        var removed = store.TryRemove(alterId, out _);
        if (removed && _polls != null)
        {
            await _polls.RemoveAlterFromPollsAsync(systemId, alterId, cancellationToken);
        }

        return removed;
    }

    public async Task<IReadOnlyList<AlterReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        if (!TryGetStore(systemId, out var store))
        {
            return Array.Empty<AlterReadModel>();
        }

        var definitions = await ResolveVisibleDefinitionsAsync(systemId, FriendshipLevel.TrustedFriend, cancellationToken);

        var rows = store.Values
            .OrderBy(x => x.AlterId.Value)
            .Select(x => MapAlterReadModel(x, definitions))
            .ToArray();

        return rows;
    }

    public async Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var friendshipLevel = await InMemoryStorageKeys.ResolveFriendshipLevelAsync(systemId, viewerSystemId, _friendships, cancellationToken);
        var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);

        var ownerId = InMemoryStorageKeys.Normalize(systemId).Value;
        if (!TryGetStore(systemId, out var store))
        {
            GuardedInstrumentation.RecordList(_logger, "alter", nameof(ListGuardedAsync), viewerSystemId, ownerId, totalCount: 0, visibleCount: 0, sw.Elapsed.TotalMilliseconds);
            return Array.Empty<BareAlter>();
        }

        var totalCount = store.Values.Count;
        var rows = store.Values
            .Where(x => x.VisibilityLevel.CanBeViewedBy(friendshipLevel))
            .OrderBy(x => x.AlterId.Value)
            .Select(x => new BareAlter(
                x.AlterId,
                x.Name,
                x.AvatarUrl,
                x.AvatarSource,
                x.Color,
                x.Pronouns,
                x.Description,
                AlterFieldProjection.ResolveGuardedFields(x.Fields, definitions, _logger)))
            .ToArray();

        GuardedInstrumentation.RecordList(_logger, "alter", nameof(ListGuardedAsync), viewerSystemId, ownerId, totalCount, rows.Length, sw.Elapsed.TotalMilliseconds);
        return rows;
    }

    public async Task<AlterReadModel?> GetAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        if (!TryGetAlter(systemId, alterId, out var alter))
        {
            return null;
        }

        var definitions = await ResolveVisibleDefinitionsAsync(systemId, FriendshipLevel.TrustedFriend, cancellationToken);

        return MapAlterReadModel(alter, definitions);
    }

    public async Task<BareAlter?> GetGuardedAsync(
        SystemId systemId,
        AlterId alterId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ownerId = InMemoryStorageKeys.Normalize(systemId).Value;
        var friendshipLevel = await InMemoryStorageKeys.ResolveFriendshipLevelAsync(systemId, viewerSystemId, _friendships, cancellationToken);
        var definitions = await ResolveVisibleDefinitionsAsync(systemId, friendshipLevel, cancellationToken);
        if (!TryGetAlter(systemId, alterId, out var alter))
        {
            GuardedInstrumentation.RecordGet(_logger, "alter", nameof(GetGuardedAsync), viewerSystemId, ownerId, alterId.Value.ToString(), found: false, filtered: false, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        if (!alter.VisibilityLevel.CanBeViewedBy(friendshipLevel))
        {
            GuardedInstrumentation.RecordGet(_logger, "alter", nameof(GetGuardedAsync), viewerSystemId, ownerId, alterId.Value.ToString(), found: false, filtered: true, sw.Elapsed.TotalMilliseconds);
            return null;
        }

        var result = new BareAlter(
            alter.AlterId,
            alter.Name,
            alter.AvatarUrl,
            alter.AvatarSource,
            alter.Color,
            alter.Pronouns,
            alter.Description,
            AlterFieldProjection.ResolveGuardedFields(alter.Fields, definitions, _logger));
        GuardedInstrumentation.RecordGet(_logger, "alter", nameof(GetGuardedAsync), viewerSystemId, ownerId, alterId.Value.ToString(), found: true, filtered: false, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    public Task<bool> AliasTakenByOtherAsync(
        SystemId systemId,
        AlterId alterId,
        string alias,
        CancellationToken cancellationToken = default
    )
    {
        if (!TryGetStore(systemId, out var store))
        {
            return Task.FromResult(false);
        }

        var taken = store.Values.Any(a =>
            a.AlterId != alterId &&
            !string.IsNullOrWhiteSpace(a.Alias) &&
            string.Equals(a.Alias, alias, StringComparison.OrdinalIgnoreCase)
        );

        return Task.FromResult(taken);
    }

    internal void RemoveFieldValuesForSystem(Guid fieldId, ScopedSystemId systemKey)
    {
        if (!TryGetStore(systemKey, out var store))
            return;

        FieldId fieldKey = new(fieldId);
        foreach (var kv in store)
        {
            var state = kv.Value;
            lock (state.Fields)
            {
                if (state.Fields.ContainsKey(fieldKey))
                {
                    state.Fields.Remove(fieldKey);
                }
            }
        }
    }

    internal void RemoveFieldValuesForSystem(SystemId systemId, Guid fieldId)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        RemoveFieldValuesForSystem(fieldId, systemKey);
    }

    private static AlterReadModel MapAlterReadModel(
        AlterState alter,
        IReadOnlyList<SettingsFieldReadModel> definitions)
    {
        return new AlterReadModel(
            alter.AlterId,
            alter.Name,
            alter.Description,
            alter.AvatarUrl,
            alter.AvatarSource,
            alter.Color,
            alter.Pronouns,
            alter.VisibilityLevel,
            AlterFieldProjection.ResolveGuardedFields(alter.Fields, definitions),
            alter.ProxyName,
            alter.Alias,
            alter.Untracked,
            alter.Archived,
            alter.Pinned,
            alter.DiscordProxies.ToArray(),
            alter.InsertedAt,
            alter.UpdatedAt);
    }

    // Short-circuits when _settingsFields is null so the shared helper keeps a non-null repo contract.
    private Task<IReadOnlyList<SettingsFieldReadModel>> ResolveVisibleDefinitionsAsync(
        SystemId systemId,
        FriendshipLevel? friendshipLevel,
        CancellationToken cancellationToken)
        => _settingsFields is null
            ? Task.FromResult<IReadOnlyList<SettingsFieldReadModel>>(Array.Empty<SettingsFieldReadModel>())
            : AlterFieldProjection.ResolveVisibleDefinitionsAsync(_settingsFields, systemId, friendshipLevel, cancellationToken, _logger);

    private bool TryGetStore(SystemId systemId, out ConcurrentDictionary<AlterId, AlterState> store)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        return TryGetStore(systemKey, out store);
    }

    private bool TryGetStore(ScopedSystemId systemKey, out ConcurrentDictionary<AlterId, AlterState> store)
        => _bySystem.TryGetValue(systemKey, out store!);

    private bool TryGetAlter(SystemId systemId, AlterId alterId, out AlterState alter)
    {
        if (TryGetStore(systemId, out var store) && store.TryGetValue(alterId, out alter!))
        {
            return true;
        }

        alter = null!;
        return false;
    }
}



