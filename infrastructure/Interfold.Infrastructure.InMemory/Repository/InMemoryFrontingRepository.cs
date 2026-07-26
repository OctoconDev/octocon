using System.Collections.Concurrent;
using System.Diagnostics;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Microsoft.Extensions.Logging;

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
    private readonly IFriendshipRepository _friendships;
    private readonly IAlterRepository _alters;
    private readonly ILogger<InMemoryFrontingRepository> _logger;
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<AlterId, FrontState>> _activeBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, List<FrontHistoryState>> _historyBySystem = new();
    private readonly ConcurrentDictionary<ScopedSystemId, AlterId?> _primaryBySystem = new();
    private readonly object _sync = new();

    public InMemoryFrontingRepository(IRegionContext regionContext, IFriendshipRepository friendships, IAlterRepository alters, ILogger<InMemoryFrontingRepository> logger)
    {
        _regionContext = regionContext;
        _friendships = friendships;
        _alters = alters;
        _logger = logger;
    }

    public Task<bool> IsFrontingAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            var active = TryGetActiveSet(systemKey, out var set) && set.ContainsKey(alterId);
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
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

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
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (!TryGetActiveSet(systemKey, out var set))
            {
                return Task.FromResult(false);
            }

            var removed = set.TryRemove(alterId, out var removedFront);
            if (removed && _primaryBySystem.TryGetValue(systemKey, out var currentPrimary) && currentPrimary == alterId)
            {
                _primaryBySystem[systemKey] = null;
            }

            if (removed && removedFront is not null && TryGetHistory(systemKey, out var history))
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
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (alterId is { } value)
            {
                if (!TryGetActiveSet(systemKey, out var set) || !set.ContainsKey(value))
                {
                    return Task.FromResult(false);
                }
            }

            _primaryBySystem[systemKey] = alterId;
            return Task.FromResult(true);
        }
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        IReadOnlyCollection<FrontState> activeFronts;
        AlterId? primaryId;

        lock (_sync)
        {
            if (!TryGetActiveSet(systemKey, out var set))
                return Array.Empty<FrontActiveReadModel>();

            activeFronts = set.Values.ToArray();
            _primaryBySystem.TryGetValue(systemKey, out primaryId);
        }

        var results = new List<FrontActiveReadModel>(activeFronts.Count);
        foreach (var x in activeFronts.OrderByDescending(f => f.StartedAt))
        {
            var alterModel = await _alters.GetAsync(systemId, x.AlterId, cancellationToken);
            var bareAlter = alterModel is not null
                ? new BareAlter(x.AlterId, alterModel.Name, alterModel.AvatarUrl, alterModel.AvatarSource, alterModel.Color, alterModel.Pronouns, alterModel.Description, alterModel.Fields ?? Array.Empty<AlterPublicFieldReadModel>())
                : BareAlter.CreatePlaceholder(x.AlterId);

            results.Add(new FrontActiveReadModel(
                bareAlter,
                new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, null, systemId),
                primaryId == x.AlterId));
        }

        return results;
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ownerId = InMemoryStorageKeys.Normalize(systemId).Value;
        var friendshipLevel = await InMemoryStorageKeys.ResolveFriendshipLevelAsync(systemId, viewerSystemId, _friendships, cancellationToken);
        if (!VisibilityLevel.Public.CanBeViewedBy(friendshipLevel))
        {
            GuardedInstrumentation.RecordList(_logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId, totalCount: 0, visibleCount: 0, sw.Elapsed.TotalMilliseconds);
            return Array.Empty<FrontActiveReadModel>();
        }

        var all = await ListActiveAsync(systemId, cancellationToken);
        if (all.Count == 0)
        {
            GuardedInstrumentation.RecordList(_logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId, totalCount: 0, visibleCount: 0, sw.Elapsed.TotalMilliseconds);
            return all;
        }

        var guardedAlters = await _alters.ListGuardedAsync(systemId, viewerSystemId, cancellationToken);
        var visibleIds = guardedAlters.Select(a => a.Id).ToHashSet();

        var visible = all.Where(front => visibleIds.Contains(front.Alter.Id)).ToArray();
        GuardedInstrumentation.RecordList(_logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId, all.Count, visible.Length, sw.Elapsed.TotalMilliseconds);
        return visible;
    }

    public Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        SystemId systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (!TryGetHistory(systemKey, out var history))
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

    public Task<IReadOnlyList<FrontHistoryReadModel>> ListAllAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (!TryGetHistory(systemKey, out var history))
            {
                return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(Array.Empty<FrontHistoryReadModel>());
            }

            var results = history
                .OrderByDescending(x => x.StartedAt)
                .Select(x => new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, x.EndedAt, systemId))
                .ToArray();

            return Task.FromResult<IReadOnlyList<FrontHistoryReadModel>>(results);
        }
    }

    public Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (!TryGetActiveSet(systemKey, out var set))
                return Task.FromResult<FrontActiveReadModel?>(null);

            _primaryBySystem.TryGetValue(systemKey, out var primary);

            var found = set.Values.FirstOrDefault(x => x.FrontId == frontId);
            if (found is null)
                return Task.FromResult<FrontActiveReadModel?>(null);

            return Task.FromResult<FrontActiveReadModel?>(new FrontActiveReadModel(
                BareAlter.CreatePlaceholder(found.AlterId),
                new FrontHistoryReadModel(found.FrontId, found.AlterId, found.Comment, found.StartedAt, null, systemId),
                primary == found.AlterId));
        }
    }

    public Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(SystemId systemId, FrontId frontId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (!TryGetHistory(systemKey, out var history))
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
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (!TryGetHistory(systemKey, out var history))
                return Task.FromResult(false);

            var entry = history.FirstOrDefault(x => x.FrontId == frontId);
            if (entry is null)
                return Task.FromResult(false);

            history.Remove(entry);

            if (TryGetActiveSet(systemKey, out var active)
                && active.TryGetValue(entry.AlterId, out var frontState)
                && frontState.FrontId == entry.FrontId)
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
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);

        lock (_sync)
        {
            if (!TryGetActiveSet(systemKey, out var set))
                return Task.FromResult(false);

            var found = set.Values.FirstOrDefault(x => x.FrontId == frontId);
            if (found is null)
                return Task.FromResult(false);

            found.Comment = comment;

            if (TryGetHistory(systemKey, out var history))
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

    private bool TryGetActiveSet(ScopedSystemId systemKey, out ConcurrentDictionary<AlterId, FrontState> set)
        => _activeBySystem.TryGetValue(systemKey, out set!);

    private bool TryGetHistory(ScopedSystemId systemKey, out List<FrontHistoryState> history)
        => _historyBySystem.TryGetValue(systemKey, out history!);

}



