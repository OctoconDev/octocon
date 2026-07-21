using System.Collections.Concurrent;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Abstractions;
using Interfold.Contracts.Enums;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryJournalRepository : IJournalRepository
{
    private sealed class EntryState
    {
        public required EntryId EntryId { get; init; }
        public required SystemId UserId { get; init; }
        public required string Title { get; set; }
        public string? Content { get; set; }
        public HexColor? Color { get; set; }
        public required DateTime InsertedAt { get; init; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class AlterEntryState
    {
        public required EntryId EntryId { get; init; }
        public required SystemId UserId { get; init; }
        public required AlterId AlterId { get; init; }
        public required string Title { get; set; }
        public string? Content { get; set; }
        public HexColor? Color { get; set; }
        public bool Pinned { get; set; }
        public bool Locked { get; set; }
        public required DateTime InsertedAt { get; init; }
        public DateTime UpdatedAt { get; set; }
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<EntryId, EntryState>> _bySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<EntryId, (bool Pinned, bool Locked)>> _stateBySystem = new();
    // Keyed on the (ScopedSystemId, EntryId) tuple rather than a hand-concatenated
    // "{systemKey}:{entryId}" string — ValueTuple gives structural equality for free.
    private readonly ConcurrentDictionary<(ScopedSystemId System, EntryId EntryId), ConcurrentDictionary<AlterId, bool>> _entryAlters = new();
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<EntryId, AlterEntryState>> _alterEntriesBySystem = new();

    public InMemoryJournalRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<EntryId?> CreateGlobalAsync(SystemId systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<EntryId, EntryState>());
        EntryId id = new(Guid.NewGuid());
        var now = DateTime.UtcNow;

        store[id] = new EntryState
        {
            EntryId = id,
            UserId = systemId,
            Title = command.Title,
            Content = null,
            Color = null,
            InsertedAt = now,
            UpdatedAt = now
        };

        return Task.FromResult<EntryId?>(id);
    }

    public Task<bool> ExistsGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        var exists = GlobalEntryExists(systemId, entryId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateGlobalAsync(SystemId systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        if (!TryGetGlobalEntry(systemId, command.EntryId, out var entry))
            return Task.FromResult(false);

        if (command.Title is not null) entry.Title = command.Title;
        if (command.Content is not null) entry.Content = command.Content;
        if (command.Color is not null) entry.Color = command.Color;
        entry.UpdatedAt = DateTime.UtcNow;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (!TryGetGlobalStore(systemId, out var store))
            return Task.FromResult(false);

        var removed = store.TryRemove(entryId, out _);
        if (removed)
        {
            if (_stateBySystem.TryGetValue(systemKey, out var stateStore))
                stateStore.TryRemove(entryId, out _);

            _entryAlters.TryRemove(GetEntryKey(systemId, entryId), out _);
        }

        return Task.FromResult(removed);
    }

    public Task<bool> SetGlobalLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (!GlobalEntryExists(systemId, entryId))
            return Task.FromResult(false);

        var stateStore = _stateBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<EntryId, (bool Pinned, bool Locked)>());
        var current = stateStore.GetOrAdd(entryId, _ => (false, false));
        stateStore[entryId] = (current.Pinned, locked);
        return Task.FromResult(true);
    }

    public Task<bool> SetGlobalPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (!GlobalEntryExists(systemId, entryId))
            return Task.FromResult(false);

        var stateStore = _stateBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<EntryId, (bool Pinned, bool Locked)>());
        var current = stateStore.GetOrAdd(entryId, _ => (false, false));
        stateStore[entryId] = (pinned, current.Locked);
        return Task.FromResult(true);
    }

    public Task<bool> AttachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        if (!GlobalEntryExists(systemId, entryId))
            return Task.FromResult(false);

        var key = GetEntryKey(systemId, entryId);
        var alters = _entryAlters.GetOrAdd(key, _ => new ConcurrentDictionary<AlterId, bool>());
        alters[alterId] = true;
        return Task.FromResult(true);
    }

    public Task<bool> DetachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        var key = GetEntryKey(systemId, entryId);
        if (!TryGetEntryAlterMap(key, out var alters))
            return Task.FromResult(false);

        return Task.FromResult(alters.TryRemove(alterId, out _));
    }

    public Task<EntryId?> CreateAlterAsync(SystemId systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var store = _alterEntriesBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<EntryId, AlterEntryState>());
        EntryId entryId = new(Guid.NewGuid());
        var now = DateTime.UtcNow;

        store[entryId] = new AlterEntryState
        {
            EntryId = entryId,
            UserId = systemId,
            AlterId = command.AlterId,
            Title = command.Title,
            Content = null,
            Color = null,
            Pinned = false,
            Locked = false,
            InsertedAt = now,
            UpdatedAt = now
        };

        return Task.FromResult<EntryId?>(entryId);
    }

    public Task<AlterJournalRef?> GetAlterRefAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        if (!TryGetAlterEntry(systemId, entryId, out var entry))
            return Task.FromResult<AlterJournalRef?>(null);

        return Task.FromResult<AlterJournalRef?>(new AlterJournalRef(entry.EntryId, entry.AlterId));
    }

    public Task<bool> UpdateAlterAsync(SystemId systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        if (!TryGetAlterEntry(systemId, command.EntryId, out var entry))
            return Task.FromResult(false);

        if (command.Title is not null) entry.Title = command.Title;
        if (command.Content is not null) entry.Content = command.Content;
        if (command.Color is not null) entry.Color = command.Color;
        entry.UpdatedAt = DateTime.UtcNow;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        if (!TryGetAlterStore(systemId, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.TryRemove(entryId, out _));
    }

    public Task<int> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        var removedAlterJournals = 0;

        if (TryGetAlterStore(systemId, out var store))
        {
            // Snapshot the keys to remove first; mutating the ConcurrentDictionary while
            // enumerating its values is undefined behaviour (entries may be skipped or
            // double-visited).
            var toRemove = store.Values.Where(e => e.AlterId == alterId).Select(e => e.EntryId).ToArray();
            foreach (var entryId in toRemove)
            {
                if (store.TryRemove(entryId, out _))
                {
                    removedAlterJournals++;
                }
            }
        }

        // Detach from any global journals this alter was attached to. _entryAlters is keyed
        // by (system, entry) and the inner dictionary's keys are alter ids; we don't need
        // to know which global entries exist - just sweep every inner dict for this alter.
        // Mirrors the Scylla path's global_journal_alters cleanup, where the row identity
        // is (user_id, global_journal_id, alter_id) and only the alter_id rows are deleted.
        foreach (var alters in _entryAlters.Values)
        {
            alters.TryRemove(alterId, out _);
        }

        return Task.FromResult(removedAlterJournals);
    }

    public Task<bool> SetAlterLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default)
        => SetAlterFlagAsync(systemId, entryId, static (entry, value) => entry.Locked = value, locked);

    public Task<bool> SetAlterPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default)
        => SetAlterFlagAsync(systemId, entryId, static (entry, value) => entry.Pinned = value, pinned);

    public Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        if (!TryGetAlterStore(systemId, out var store))
            return Task.FromResult<IReadOnlyList<AlterJournalReadModel>>(Array.Empty<AlterJournalReadModel>());

        var entries = store.Values
            .Where(e => e.AlterId == alterId)
            .OrderByDescending(e => e.InsertedAt)
            .Select(MapAlterJournalReadModel)
            .ToArray();

        return Task.FromResult<IReadOnlyList<AlterJournalReadModel>>(entries);
    }

    public Task<AlterJournalReadModel?> GetAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        if (!TryGetAlterEntry(systemId, entryId, out var entry))
            return Task.FromResult<AlterJournalReadModel?>(null);

        return Task.FromResult<AlterJournalReadModel?>(MapAlterJournalReadModel(entry));
    }

    private Task<bool> SetAlterFlagAsync(
        SystemId systemId,
        EntryId entryId,
        Action<AlterEntryState, bool> apply,
        bool value)
    {
        if (!TryGetAlterEntry(systemId, entryId, out var entry))
        {
            return Task.FromResult(false);
        }

        apply(entry, value);
        entry.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    private bool TryGetAlterEntry(SystemId systemId, EntryId entryId, out AlterEntryState entry)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (_alterEntriesBySystem.TryGetValue(systemKey, out var store)
            && store.TryGetValue(entryId, out entry!))
        {
            return true;
        }

        entry = null!;
        return false;
    }

    public Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        if (!TryGetGlobalStore(systemId, out var store))
            return Task.FromResult<IReadOnlyList<JournalReadModel>>(Array.Empty<JournalReadModel>());

        // Sort key is the wire form (lowercase "N" hex) to keep list ordering byte-identical
        // to the historic string-backed EntryId — Guid.CompareTo bytewise reorders differently.
        var entries = store.Values
            .OrderByDescending(e => e.EntryId.Value.ToString("N"), StringComparer.Ordinal)
            .Select(e =>
            {
                var (pinned, locked) = GetGlobalState(systemId, e.EntryId);
                var alterIds = GetGlobalAlterIds(systemId, e.EntryId);
                return MapJournalReadModel(e, locked, pinned, alterIds);
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<JournalReadModel>>(entries);
    }

    public Task<JournalReadModel?> GetGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default)
    {
        if (!TryGetGlobalEntry(systemId, entryId, out var entry))
            return Task.FromResult<JournalReadModel?>(null);

        var (pinned, locked) = GetGlobalState(systemId, entryId);
        var alterIds = GetGlobalAlterIds(systemId, entryId);
        return Task.FromResult<JournalReadModel?>(MapJournalReadModel(entry, locked, pinned, alterIds));
    }

    private (bool Pinned, bool Locked) GetGlobalState(SystemId systemId, EntryId entryId)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        if (_stateBySystem.TryGetValue(systemKey, out var stateStore) &&
            stateStore.TryGetValue(entryId, out var state))
            return state;
        return (false, false);
    }

    private IReadOnlyList<AlterId> GetGlobalAlterIds(SystemId systemId, EntryId entryId)
    {
        var key = GetEntryKey(systemId, entryId);
        if (!TryGetEntryAlterMap(key, out var alters))
            return Array.Empty<AlterId>();
        return alters.Keys.ToArray();
    }

    // Typed tuple key for the _entryAlters dict, replacing a stringly-typed concat.
    private (ScopedSystemId System, EntryId EntryId) GetEntryKey(SystemId systemId, EntryId entryId)
        => (InMemoryStorageKeys.ForSystem(_regionContext, systemId), entryId);

    private static AlterJournalReadModel MapAlterJournalReadModel(AlterEntryState entry)
    {
        return new AlterJournalReadModel(
            entry.EntryId,
            entry.UserId,
            entry.AlterId,
            entry.Title,
            entry.Content,
            entry.Color,
            entry.Locked,
            entry.Pinned,
            entry.InsertedAt,
            entry.UpdatedAt
        );
    }

    private static JournalReadModel MapJournalReadModel(EntryState entry, bool locked, bool pinned, IReadOnlyList<AlterId> alterIds)
    {
        return new JournalReadModel(
            entry.EntryId,
            entry.UserId,
            entry.Title,
            entry.Content,
            entry.Color,
            locked,
            pinned,
            entry.InsertedAt,
            entry.UpdatedAt,
            alterIds
        );
    }

    private bool TryGetGlobalStore(SystemId systemId, out ConcurrentDictionary<EntryId, EntryState> store)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        return _bySystem.TryGetValue(systemKey, out store!);
    }

    private bool TryGetAlterStore(SystemId systemId, out ConcurrentDictionary<EntryId, AlterEntryState> store)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        return _alterEntriesBySystem.TryGetValue(systemKey, out store!);
    }

    private bool TryGetEntryAlterMap(
        (ScopedSystemId System, EntryId EntryId) key,
        out ConcurrentDictionary<AlterId, bool> alters)
        => _entryAlters.TryGetValue(key, out alters!);

    private bool TryGetGlobalEntry(SystemId systemId, EntryId entryId, out EntryState entry)
    {
        if (TryGetGlobalStore(systemId, out var store) && store.TryGetValue(entryId, out entry!))
        {
            return true;
        }

        entry = null!;
        return false;
    }

    private bool GlobalEntryExists(SystemId systemId, EntryId entryId)
        => TryGetGlobalStore(systemId, out var store) && store.ContainsKey(entryId);
}

