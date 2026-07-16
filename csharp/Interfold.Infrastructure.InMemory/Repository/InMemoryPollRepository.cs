using System.Collections.Concurrent;
using System.Text.Json;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Abstractions;
using Interfold.Contracts.Models;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryPollRepository : IPollRepository
{
    private sealed class PollState
    {
        public required PollId PollId { get; init; }
        public required SystemId UserId { get; init; }
        public required string Title { get; set; }
        public string? Description { get; set; }
        public required PollType Type { get; set; }
        public JsonElement Data { get; set; } = JsonElement.Parse("{}");
        public DateTime? TimeEnd { get; set; }
        public DateTime InsertedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<PollId, PollState>> _bySystem = new();

    public InMemoryPollRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<IReadOnlyList<PollReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult<IReadOnlyList<PollReadModel>>(Array.Empty<PollReadModel>());

        // Sort key is the wire form (lowercase "N" hex) to keep list ordering byte-identical
        // to the historic string-backed PollId — Guid.CompareTo bytewise reorders differently.
        var list = store.Values
            .Select(ToReadModel)
            .OrderBy(p => p.Id.Value.ToString("N"), StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<PollReadModel>>(list);
    }

    public Task<PollReadModel?> GetAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(pollId, out var poll))
            return Task.FromResult<PollReadModel?>(null);

        return Task.FromResult<PollReadModel?>(ToReadModel(poll));
    }

    public Task<PollId?> CreateAsync(SystemId systemId, CreatePollCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<PollId, PollState>());
        PollId id = new(Guid.NewGuid());

        store[id] = new PollState
        {
            PollId = id,
            UserId = systemId,
            Title = command.Title,
            Description = command.Description,
            Type = command.Type,
            TimeEnd = command.TimeEnd,
            InsertedAt = command.InsertedAtUtc,
            UpdatedAt = DateTime.UtcNow
        };

        return Task.FromResult<PollId?>(id);
    }

    public Task<bool> ExistsAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(pollId);
        return Task.FromResult(exists);
    }

    public Task<bool> UpdateAsync(SystemId systemId, UpdatePollCommand command, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.Id, out var poll))
            return Task.FromResult(false);

        if (command.Title is not null) poll.Title = command.Title;
        if (command.Description is not null) poll.Description = command.Description;
        if (command.HasTimeEnd) poll.TimeEnd = command.TimeEnd;
        if (command.Data is not null) poll.Data = command.Data.Value;
        poll.UpdatedAt = DateTime.UtcNow;

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(SystemId systemId, PollId pollId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.FromResult(false);

        return Task.FromResult(store.TryRemove(pollId, out _));
    }

    // The data blob's confirmed shape (see PollDataJson) keeps per-alter votes in the
    // top-level `responses` array as {"alter_id":<int>,...} entries; deleting an alter
    // filters those entries while leaving every other member untouched.
    public Task RemoveAlterFromPollsAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        if (!_bySystem.TryGetValue(systemKey, out var store))
            return Task.CompletedTask;

        foreach (var poll in store.Values)
        {
            if (PollDataJson.TryRemoveAlterResponses(poll.Data, alterId, out var newData))
            {
                poll.Data = newData;
                poll.UpdatedAt = DateTime.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    private static PollReadModel ToReadModel(PollState state)
        => new(
            state.PollId,
            state.UserId,
            state.Title,
            state.Description,
            state.Type,
            state.Data,
            state.TimeEnd,
            state.InsertedAt,
            state.UpdatedAt
        );

    private ScopedSystemId GetSystemKey(SystemId systemId) => InMemoryStorageKeys.ForSystem(_regionContext, systemId);
}
