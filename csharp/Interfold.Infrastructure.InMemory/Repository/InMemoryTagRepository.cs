using System.Collections.Concurrent;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryTagRepository : ITagRepository
{
   private sealed record TagState(
       TagId TagId,
       TagId? ParentTagId,
       string Name,
       DateTime InsertedAt,
       DateTime UpdatedAt,
       HexColor? Color = null,
       string? Description = null,
       VisibilityLevel SecurityLevel = VisibilityLevel.Private
   );

    private readonly IRegionContext _regionContext;
    private readonly IFriendshipRepository? _friendships;
    private readonly ConcurrentDictionary<ScopedSystemId, ConcurrentDictionary<TagId, TagState>> _bySystem = new();
    // Keyed on the (ScopedSystemId, TagId) tuple rather than a hand-concatenated
    // "{systemKey}:{tagId}" string — ValueTuple gives structural equality for free.
    private readonly ConcurrentDictionary<(ScopedSystemId System, TagId TagId), ConcurrentDictionary<BareAlter, bool>> _alterMemberships = new();

    public InMemoryTagRepository(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public InMemoryTagRepository(IRegionContext regionContext, IFriendshipRepository friendships)
    {
        _regionContext = regionContext;
        _friendships = friendships;
    }

    public Task<TagId?> CreateAsync(
        SystemId systemId,
        CreateTagCommand command,
        CancellationToken cancellationToken = default
    )
    {
        var systemKey = GetSystemKey(systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<TagId, TagState>());

        if (command.ParentTagId is { } parent && parent != TagId.Empty && !store.ContainsKey(parent))
            return Task.FromResult<TagId?>(null);

        TagId id = new(Guid.NewGuid());
        store[id] = new TagState(id, command.ParentTagId, command.Name, command.InsertedAtUtc, DateTime.UtcNow);
        return Task.FromResult<TagId?>(id);
    }

    public Task<bool> ExistsAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
    {
        var systemKey = GetSystemKey(systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(tagId);
        return Task.FromResult(exists);
    }

       public Task<bool> UpdateAsync(SystemId systemId, UpdateTagCommand command, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(command.TagId, out var existing))
               return Task.FromResult(false);

           store[command.TagId] = existing with
           {
               Name        = command.Name        ?? existing.Name,
               UpdatedAt   = DateTime.UtcNow,
               Color       = command.Color       ?? existing.Color,
               Description = command.Description ?? existing.Description,
               SecurityLevel = command.SecurityLevel ?? existing.SecurityLevel
           };
           return Task.FromResult(true);
       }

       public Task<bool> DeleteAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Task.FromResult(false);

           var removed = store.TryRemove(tagId, out _);
           if (removed)
               _alterMemberships.TryRemove((systemKey, tagId), out _);
           return Task.FromResult(removed);
       }

       public Task<bool> AttachAlterAsync(SystemId systemId, TagId tagId, AlterId alterId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.ContainsKey(tagId))
               return Task.FromResult(false);

           var members = _alterMemberships.GetOrAdd((systemKey, tagId), _ => new ConcurrentDictionary<BareAlter, bool>());
           members[new BareAlter(alterId, "", null, null, null, null, null, null!)] = true;
           return Task.FromResult(true);
       }

       public Task<bool> DetachAlterAsync(SystemId systemId, TagId tagId, AlterId alterId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_alterMemberships.TryGetValue((systemKey, tagId), out var members))
               return Task.FromResult(false);

           return Task.FromResult(members.Remove(members.FirstOrDefault(x => x.Key.Id == alterId).Key, out _));
       }

       public Task<TagId?> GetParentIdAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (_bySystem.TryGetValue(systemKey, out var store) && store.TryGetValue(tagId, out var state))
               return Task.FromResult(state.ParentTagId);

           return Task.FromResult<TagId?>(null);
       }

       public Task<bool> SetParentAsync(SystemId systemId, TagId tagId, TagId parentTagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Task.FromResult(false);

           if (!store.TryGetValue(tagId, out var existing) || !store.ContainsKey(parentTagId))
               return Task.FromResult(false);

           store[tagId] = existing with { ParentTagId = parentTagId, UpdatedAt = DateTime.UtcNow };
           return Task.FromResult(true);
       }

       public Task<bool> RemoveParentAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(tagId, out var existing))
               return Task.FromResult(false);

           store[tagId] = existing with { ParentTagId = null, UpdatedAt = DateTime.UtcNow };
           return Task.FromResult(true);
       }

       public Task<IReadOnlyList<TagReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Task.FromResult<IReadOnlyList<TagReadModel>>(Array.Empty<TagReadModel>());

           // Sort key is the wire form (lowercase "N" hex) to keep list ordering byte-identical
           // to the historic string-backed TagId — Guid.CompareTo bytewise reorders differently.
           var rows = store.Values
               .OrderBy(x => x.TagId.Value.ToString("N"), StringComparer.Ordinal)
               .Select(x => new TagReadModel(
                   x.TagId,
                   x.Name,
                   x.Color,
                   x.Description,
                   x.ParentTagId,
                   GetAlterIds(systemKey, x.TagId),
                   x.InsertedAt,
                   x.UpdatedAt,
                   x.SecurityLevel,
                   systemId))
               .ToArray();

           return Task.FromResult<IReadOnlyList<TagReadModel>>(rows);
       }

       public async Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
           SystemId systemId,
           SystemId? viewerSystemId,
           CancellationToken cancellationToken = default)
       {
           var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store))
               return Array.Empty<TagPublicReadModel>();

           var rows = store.Values
               .Where(x => x.SecurityLevel.CanBeViewedBy(friendshipLevel))
               .OrderBy(x => x.TagId.Value.ToString("N"), StringComparer.Ordinal)
               .Select(x => new TagPublicReadModel(
                   x.TagId,
                   x.Name,
                   x.Color,
                   x.Description,
                   x.ParentTagId,
                   GetAlters(systemKey, x.TagId),
                   x.InsertedAt,
                   x.UpdatedAt,
                   x.SecurityLevel,
                   systemId))
               .ToArray();

           return rows;
       }

       public Task<TagReadModel?> GetAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(tagId, out var tag))
               return Task.FromResult<TagReadModel?>(null);

           return Task.FromResult<TagReadModel?>(new TagReadModel(
               tag.TagId,
               tag.Name,
               tag.Color,
               tag.Description,
               tag.ParentTagId,
               GetAlterIds(systemKey, tag.TagId),
               tag.InsertedAt,
               tag.UpdatedAt,
               tag.SecurityLevel,
               systemId));
       }

       public async Task<TagPublicReadModel?> GetGuardedAsync(
           SystemId systemId,
           TagId tagId,
           SystemId? viewerSystemId,
           CancellationToken cancellationToken = default)
       {
           var friendshipLevel = await ResolveFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
           var systemKey = GetSystemKey(systemId);
           if (!_bySystem.TryGetValue(systemKey, out var store) || !store.TryGetValue(tagId, out var tag))
           {
               return null;
           }

           if (!tag.SecurityLevel.CanBeViewedBy(friendshipLevel))
           {
               return null;
           }

           return new TagPublicReadModel(
               tag.TagId,
               tag.Name,
               tag.Color,
               tag.Description,
               tag.ParentTagId,
               GetAlters(systemKey, tag.TagId),
               tag.InsertedAt,
               tag.UpdatedAt,
               tag.SecurityLevel,
               systemId);
       }

    private IReadOnlyList<AlterId> GetAlterIds(ScopedSystemId systemKey, TagId tagId)
    {
        if (!_alterMemberships.TryGetValue((systemKey, tagId), out var members))
            return Array.Empty<AlterId>();

        return members.Keys.OrderBy(x => x.Id.Value).Select(x => x.Id).ToArray();
    }

    private IReadOnlyList<BareAlter> GetAlters(ScopedSystemId systemKey, TagId tagId)
    {
        if (!_alterMemberships.TryGetValue((systemKey, tagId), out var members))
            return Array.Empty<BareAlter>();

        return members.Keys.OrderBy(x => x.Id.Value).ToArray();
    }

    private ScopedSystemId GetSystemKey(SystemId systemId) => InMemoryStorageKeys.ForSystem(_regionContext, systemId);

    // Delegates to the shared static that also serves the Alter and Fronting repos —
    // see InMemoryStorageKeys.ResolveFriendshipLevelAsync for the self-check semantics.
    private Task<FriendshipLevel?> ResolveFriendshipLevelAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken)
        => InMemoryStorageKeys.ResolveFriendshipLevelAsync(systemId, viewerSystemId, _friendships, cancellationToken);
}
