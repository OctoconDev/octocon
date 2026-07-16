using System.Collections.Concurrent;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryFrontingRepository : IFrontingRepository
{
    private sealed class FrontState
    {
        public required FrontId FrontId { get; init; }
        public required AlterId AlterId { get; init; }
        public string? Comment { get; set; }
        public required DateTimeOffset StartedAt { get; init; }
    }

    private sealed class FrontHistoryState
    {
        public required FrontId FrontId { get; init; }
        public required AlterId AlterId { get; init; }
        public string? Comment { get; set; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; set; }
    }

    private readonly IRegionContext _regionContext;
    private readonly IFriendshipRepository? _friendships;
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<AlterId, FrontState>> _activeBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, List<FrontHistoryState>> _historyBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, AlterId?> _primaryBySystem = new();
    private readonly object _sync = new();

    public InMemoryFrontingRepository(IRegionContext regionContext, IFriendshipRepository friendships)
    {
        _regionContext = regionContext;
        _friendships = friendships;
    }

    public Task<bool> IsFrontingAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            var active = _activeBySystem.TryGetValue(systemKey, out var set) && set.ContainsKey(alterId);
            return Task.FromResult(active);
        }
    }

    public Task<FrontId?> StartAsync(
        SystemId systemId,
        AlterId alterId,
        string? comment,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            var set = _activeBySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<AlterId, FrontState>());
            if (set.ContainsKey(alterId))
            {
                return Task.FromResult<FrontId?>(null);
            }

            FrontId frontId = new(Guid.NewGuid());
            set[alterId] = new FrontState
            {
                FrontId = frontId,
                AlterId = alterId,
                Comment = comment,
                StartedAt = startedAt
            };

            var history = _historyBySystem.GetOrAdd(systemKey, _ => new List<FrontHistoryState>());
            history.Add(new FrontHistoryState
            {
                FrontId = frontId,
                AlterId = alterId,
                Comment = comment,
                StartedAt = set[alterId].StartedAt,
                EndedAt = null
            });

            return Task.FromResult<FrontId?>(frontId);
        }
    }

    public Task<bool> EndAsync(SystemId systemId, AlterId alterId, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
            {
                return Task.FromResult(false);
            }

            var removed = set.TryRemove(alterId, out var removedFront);
            if (removed && _primaryBySystem.TryGetValue(systemKey, out var currentPrimary) && currentPrimary == alterId)
            {
                _primaryBySystem[systemKey] = null;
            }

            if (removed && removedFront is not null && _historyBySystem.TryGetValue(systemKey, out var history))
            {
                var historical = history.LastOrDefault(
                    x => x.FrontId == removedFront.FrontId &&
                         x.EndedAt is null);
                if (historical is not null)
                {
                    historical.EndedAt = endedAt;
                }
            }

            return Task.FromResult(removed);
        }
    }

    public Task<bool> SetPrimaryAsync(SystemId systemId, AlterId? alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (alterId is { } value)
            {
                if (!_activeBySystem.TryGetValue(systemKey, out var set) || !set.ContainsKey(value))
                {
                    return Task.FromResult(false);
                }
            }

            _primaryBySystem[systemKey] = alterId;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
                return Task.FromResult<IReadOnlyList<FrontActiveReadModel>>(Array.Empty<FrontActiveReadModel>());

            _primaryBySystem.TryGetValue(systemKey, out var primary);

            var results = set.Values
                .Select(x => new FrontActiveReadModel(
                    new BareAlter(x.AlterId, $"Alter {x.AlterId}", null, null, null, null, null, null!),
                    new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, null, systemId),
                    primary == x.AlterId))
                .OrderByDescending(x => x.Front.TimeStart)
                .ToArray();

            return Task.FromResult<IReadOnlyList<FrontActiveReadModel>>(results);
        }
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
        if (!VisibilityLevel.Public.CanBeViewedBy(friendshipLevel))
        {
            return Array.Empty<FrontActiveReadModel>();
        }

        return await ListActiveAsync(systemId, cancellationToken);
    }

    public Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        SystemId systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_historyBySystem.TryGetValue(systemKey, out var history))
            {
                return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(Array.Empty<FrontHistoryReadModel>());
            }

            var results = history
                .Where(x => x.StartedAt >= startInclusive && x.StartedAt <= endInclusive)
                .OrderByDescending(x => x.StartedAt)
                .Select(x => new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, x.EndedAt, systemId))
                .ToArray();

            return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(results);
        }
    }

    public Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
                return Task.FromResult<FrontActiveReadModel?>(null);

            _primaryBySystem.TryGetValue(systemKey, out var primary);

            var found = set.Values.FirstOrDefault(x => x.FrontId == frontId);
            if (found is null)
                return Task.FromResult<FrontActiveReadModel?>(null);

            return Task.FromResult<FrontActiveReadModel?>(new FrontActiveReadModel(
                new BareAlter(found.AlterId, $"Alter {found.AlterId}", null, null, null, null, null, null!),
                new FrontHistoryReadModel(found.FrontId, found.AlterId, found.Comment, found.StartedAt, null, systemId),
                primary == found.AlterId));
        }
    }

    public Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_historyBySystem.TryGetValue(systemKey, out var history))
                return Task.FromResult<FrontHistoryReadModel?>(null);

            var entry = history.FirstOrDefault(x => x.FrontId == frontId);
            if (entry is null)
                return Task.FromResult<FrontHistoryReadModel?>(null);

            return Task.FromResult<FrontHistoryReadModel?>(new FrontHistoryReadModel(
                entry.FrontId, entry.AlterId, entry.Comment, entry.StartedAt, entry.EndedAt, systemId));
        }
    }

    public async Task<bool> EndByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        var found = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
        if (found is null)
            return false;

        return await EndAsync(systemId, found.Front.AlterId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public Task<bool> DeleteFrontByIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_historyBySystem.TryGetValue(systemKey, out var history))
                return Task.FromResult(false);

            var entry = history.FirstOrDefault(x => x.FrontId == frontId);
            if (entry is null)
                return Task.FromResult(false);

            history.Remove(entry);

            if (_activeBySystem.TryGetValue(systemKey, out var active) && active.ContainsKey(entry.AlterId))
            {
                active.TryRemove(entry.AlterId, out _);

                if (_primaryBySystem.TryGetValue(systemKey, out var primary) && primary == entry.AlterId)
                    _primaryBySystem[systemKey] = null;
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateCommentByFrontIdAsync(SystemId systemId, FrontId frontId, string comment, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);

        lock (_sync)
        {
            if (!_activeBySystem.TryGetValue(systemKey, out var set))
                return Task.FromResult(false);

            var found = set.Values.FirstOrDefault(x => x.FrontId == frontId);
            if (found is null)
                return Task.FromResult(false);

            found.Comment = comment;

            if (_historyBySystem.TryGetValue(systemKey, out var history))
            {
                var historical = history.LastOrDefault(
                    x => x.FrontId == frontId && x.EndedAt is null);
                if (historical is not null)
                {
                    historical.Comment = comment;
                }
            }

            return Task.FromResult(true);
        }
    }

    private ScopedSystemId GetSystemKey(SystemId systemId) => InMemoryStorageKeys.ForSystem(_regionContext, systemId);

    // Delegates to the shared static that also serves the Alter and Tag repos —
    // see InMemoryStorageKeys.ResolveFriendshipLevelAsync for the self-check semantics.
    private Task<FriendshipLevel?> ResolveFriendshipLevelAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken)
        => InMemoryStorageKeys.ResolveFriendshipLevelAsync(systemId, viewerSystemId, _friendships, cancellationToken);
}