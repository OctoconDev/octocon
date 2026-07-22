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
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new ConcurrentDictionary<TagId, TagState>());

        if (command.ParentTagId is { } parent && parent != TagId.Empty && !store.ContainsKey(parent))
            return Task.FromResult<TagId?>(null);

        TagId id = new(Guid.NewGuid());
        store[id] = new TagState(id, command.ParentTagId, command.Name, command.InsertedAtUtc, DateTime.UtcNow);
        return Task.FromResult<TagId?>(id);
    }

    public Task<bool> ExistsAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var exists = _bySystem.TryGetValue(systemKey, out var store) && store.ContainsKey(tagId);
        return Task.FromResult(exists);
    }

       public Task<bool> UpdateAsync(SystemId systemId, UpdateTagCommand command, CancellationToken cancellationToken = default)
       {
           if (!TryGetTag(systemId, command.TagId, out var store, out var existing))
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
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetStore(systemKey, out var store))
               return Task.FromResult(false);

           var removed = store.TryRemove(tagId, out _);
           if (removed)
               _alterMemberships.TryRemove((systemKey, tagId), out _);
           return Task.FromResult(removed);
       }

       public Task<bool> AttachAlterAsync(SystemId systemId, TagId tagId, AlterId alterId, CancellationToken cancellationToken = default)
       {
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetStore(systemKey, out var store) || !store.ContainsKey(tagId))
               return Task.FromResult(false);

           var members = _alterMemberships.GetOrAdd((systemKey, tagId), _ => new ConcurrentDictionary<BareAlter, bool>());
           members[BareAlter.CreatePlaceholder(alterId)] = true;
           return Task.FromResult(true);
       }

       public Task<bool> DetachAlterAsync(SystemId systemId, TagId tagId, AlterId alterId, CancellationToken cancellationToken = default)
       {
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetAlterMembers(systemKey, tagId, out var members))
               return Task.FromResult(false);

           return Task.FromResult(members.Remove(members.FirstOrDefault(x => x.Key.Id == alterId).Key, out _));
       }

       public Task<TagId?> GetParentIdAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
       {
           if (TryGetTag(systemId, tagId, out _, out var state))
               return Task.FromResult(state.ParentTagId);

           return Task.FromResult<TagId?>(null);
       }

       public Task<bool> SetParentAsync(SystemId systemId, TagId tagId, TagId parentTagId, CancellationToken cancellationToken = default)
       {
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetStore(systemKey, out var store))
               return Task.FromResult(false);

           if (!store.TryGetValue(tagId, out var existing) || !store.ContainsKey(parentTagId))
               return Task.FromResult(false);

           store[tagId] = existing with { ParentTagId = parentTagId, UpdatedAt = DateTime.UtcNow };
           return Task.FromResult(true);
       }

       public Task<bool> RemoveParentAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
       {
           if (!TryGetTag(systemId, tagId, out var store, out var existing))
               return Task.FromResult(false);

           store[tagId] = existing with { ParentTagId = null, UpdatedAt = DateTime.UtcNow };
           return Task.FromResult(true);
       }

       public Task<IReadOnlyList<TagReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
       {
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetStore(systemKey, out var store))
               return Task.FromResult<IReadOnlyList<TagReadModel>>(Array.Empty<TagReadModel>());

           // Sort by wire form (lowercase "N" hex) — Guid.CompareTo reorders differently.
            var rows = store.Values
                .OrderBy(x => x.TagId.Value.ToString("N"), StringComparer.Ordinal)
                .Select(x => MapTagReadModel(x, GetAlterIds(systemKey, x.TagId), systemId))
                .ToArray();

           return Task.FromResult<IReadOnlyList<TagReadModel>>(rows);
       }

       public async Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
           SystemId systemId,
           SystemId? viewerSystemId,
           CancellationToken cancellationToken = default)
       {
           var friendshipLevel = await InMemoryStorageKeys.ResolveFriendshipLevelAsync(systemId, viewerSystemId, _friendships, cancellationToken);
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetStore(systemKey, out var store))
               return Array.Empty<TagPublicReadModel>();

           var rows = store.Values
               .Where(x => x.SecurityLevel.CanBeViewedBy(friendshipLevel))
               .OrderBy(x => x.TagId.Value.ToString("N"), StringComparer.Ordinal)
               .Select(x => MapTagPublicReadModel(x, GetAlters(systemKey, x.TagId), systemId))
               .ToArray();

           return rows;
       }

       public Task<TagReadModel?> GetAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default)
       {
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetTag(systemKey, tagId, out var store, out var tag))
               return Task.FromResult<TagReadModel?>(null);

           return Task.FromResult<TagReadModel?>(MapTagReadModel(tag, GetAlterIds(systemKey, tag.TagId), systemId));
       }

       public async Task<TagPublicReadModel?> GetGuardedAsync(
           SystemId systemId,
           TagId tagId,
           SystemId? viewerSystemId,
           CancellationToken cancellationToken = default)
       {
           var friendshipLevel = await InMemoryStorageKeys.ResolveFriendshipLevelAsync(systemId, viewerSystemId, _friendships, cancellationToken);
           var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
           if (!TryGetTag(systemKey, tagId, out var store, out var tag))
           {
               return null;
           }

           if (!tag.SecurityLevel.CanBeViewedBy(friendshipLevel))
           {
               return null;
           }

            return MapTagPublicReadModel(tag, GetAlters(systemKey, tag.TagId), systemId);
       }

    private IReadOnlyList<AlterId> GetAlterIds(ScopedSystemId systemKey, TagId tagId)
    {
        if (!TryGetAlterMembers(systemKey, tagId, out var members))
            return Array.Empty<AlterId>();

        return members.Keys.OrderBy(x => x.Id.Value).Select(x => x.Id).ToArray();
    }

    private IReadOnlyList<BareAlter> GetAlters(ScopedSystemId systemKey, TagId tagId)
    {
        if (!TryGetAlterMembers(systemKey, tagId, out var members))
            return Array.Empty<BareAlter>();

        return members.Keys.OrderBy(x => x.Id.Value).ToArray();
    }

    private static TagReadModel MapTagReadModel(TagState tag, IReadOnlyList<AlterId> alterIds, SystemId systemId)
    {
        return new TagReadModel(
            tag.TagId,
            tag.Name,
            tag.Color,
            tag.Description,
            tag.ParentTagId,
            alterIds,
            tag.InsertedAt,
            tag.UpdatedAt,
            tag.SecurityLevel,
            systemId
        );
    }

    private static TagPublicReadModel MapTagPublicReadModel(TagState tag, IReadOnlyList<BareAlter> alters, SystemId systemId)
    {
        return new TagPublicReadModel(
            tag.TagId,
            tag.Name,
            tag.Color,
            tag.Description,
            tag.ParentTagId,
            alters,
            tag.InsertedAt,
            tag.UpdatedAt,
            tag.SecurityLevel,
            systemId
        );
    }

    private bool TryGetStore(ScopedSystemId systemKey, out ConcurrentDictionary<TagId, TagState> store)
        => _bySystem.TryGetValue(systemKey, out store!);

    private bool TryGetTag(SystemId systemId, TagId tagId, out ConcurrentDictionary<TagId, TagState> store, out TagState tag)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        return TryGetTag(systemKey, tagId, out store, out tag);
    }

    private bool TryGetTag(ScopedSystemId systemKey, TagId tagId, out ConcurrentDictionary<TagId, TagState> store, out TagState tag)
    {
        if (TryGetStore(systemKey, out store) && store.TryGetValue(tagId, out tag!))
        {
            return true;
        }

        tag = null!;
        return false;
    }

    private bool TryGetAlterMembers(ScopedSystemId systemKey, TagId tagId, out ConcurrentDictionary<BareAlter, bool> members)
        => _alterMemberships.TryGetValue((systemKey, tagId), out members!);
}
