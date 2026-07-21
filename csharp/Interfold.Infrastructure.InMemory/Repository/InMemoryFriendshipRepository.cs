using System.Collections.Concurrent;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryFriendshipRepository : IFriendshipRepository
{
    private sealed class FriendshipState
    {
        public required SystemId FriendSystemId { get; init; }
        public required FriendshipLevel Level { get; set; }
        public required DateTimeOffset Since { get; init; }
    }

    private sealed class RequestState
    {
        public required SystemId OtherSystemId { get; init; }
        public required DateTimeOffset DateSent { get; init; }
    }

    private readonly ConcurrentDictionary<SystemId, ConcurrentDictionary<SystemId, FriendshipState>> _friendships = new();
    private readonly ConcurrentDictionary<SystemId, ConcurrentDictionary<SystemId, RequestState>> _outgoingRequests = new();

    /// <summary>
    /// Parameterless / find-only-account ctor used from the DI container.
    /// <paramref name="accounts"/> is accepted (and ignored) so the DI signature stays
    /// stable across the friend-request tightening that removed the Discord dispatch
    /// lane; production and test wiring can keep passing whatever they used to. A
    /// follow-up may drop the parameter once every consumer stops passing it.
    /// </summary>
    public InMemoryFriendshipRepository(IAccountRepository? accounts = null)
    {
        _ = accounts;
    }

    public Task<SystemId?> ResolveUserIdAsync(FriendLookup lookup, CancellationToken cancellationToken = default)
    {
        // Mirror ScyllaFriendshipRepository's dispatch so both backends resolve identically.
        // InMemory has no user_registry / users_by_username tables, so Kind.Id inputs
        // collapse onto "normalise and return" — tests explicitly seed users via
        // EnsureUserExistsAsync so the returned SystemId always maps to a real record.
        // Kind.Username has no reverse-index — returning null matches "no such username"
        // cleanly and lines up with the 422 friend_request:no_user surface exercised by
        // SendFriendRequestPrefixTests's InMemory branch.
        //
        // Non-id/username shapes (Discord, region-scoped, unknown-prefix, blank) never
        // reach here — FriendLookup.TryParse rejects them at route binding with a 400,
        // so this method's switch only has to cover the two remaining kinds. If a new
        // FriendLookupKind is added it needs an explicit branch: the previous silent-null
        // fallback masqueraded as "no such user" and hid the omission from every caller.
        SystemId? result = lookup.Kind switch
        {
            FriendLookupKind.Id => InMemoryStorageKeys.Normalize(new SystemId(lookup.Value)),
            FriendLookupKind.Username => null,
            _ => throw new ArgumentOutOfRangeException(nameof(lookup), lookup.Kind,
                $"Unhandled FriendLookupKind '{lookup.Kind}' in ResolveUserIdAsync."),
        };
        return Task.FromResult(result);
    }

    public Task<FriendshipLevel?> GetFriendshipLevelAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return Task.FromResult<FriendshipLevel?>(null);
        }

        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedViewerId = InMemoryStorageKeys.Normalize(viewerSystemId.Value);

        if (normalizedSystemId == normalizedViewerId)
        {
            return Task.FromResult<FriendshipLevel?>(FriendshipLevel.TrustedFriend);
        }

        if (!TryGetFriendshipState(normalizedSystemId, normalizedViewerId, out var state))
        {
            return Task.FromResult<FriendshipLevel?>(null);
        }

        return Task.FromResult<FriendshipLevel?>(state.Level);
    }

    public Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        if (!TryGetFriendStore(normalizedSystemId, out var store))
        {
            return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(Array.Empty<FriendshipReadModel>());
        }

        var list = store.Values
            .OrderByDescending(x => x.Since)
            .Select(x => new FriendshipReadModel(
                new FriendProfileReadModel(x.FriendSystemId, null, null, null, null, null),
                new FriendshipModel(
                x.Level,
                x.Since),
                Array.Empty<FriendFrontingReadModel>()))
            .ToList();

        return Task.FromResult<IReadOnlyList<FriendshipReadModel>>(list);
    }

    public Task<FriendshipReadModel?> GetFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedFriendId = InMemoryStorageKeys.Normalize(friendSystemId);

        if (!TryGetFriendshipState(normalizedSystemId, normalizedFriendId, out var state))
        {
            return Task.FromResult<FriendshipReadModel?>(null);
        }

        return Task.FromResult<FriendshipReadModel?>(new FriendshipReadModel(
            new FriendProfileReadModel(state.FriendSystemId, null, null, null, null, null),
            new FriendshipModel(
                state.Level,
                state.Since),
            Array.Empty<FriendFrontingReadModel>()));
    }

    public Task<bool> RemoveFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedFriendId = InMemoryStorageKeys.Normalize(friendSystemId);

        if (!TryGetFriendStore(normalizedSystemId, out var userStore) || !userStore.TryRemove(normalizedFriendId, out _))
        {
            return Task.FromResult(false);
        }

        if (_friendships.TryGetValue(normalizedFriendId, out var peerStore))
        {
            peerStore.TryRemove(normalizedSystemId, out _);
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetTrustedAsync(SystemId systemId, SystemId friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedFriendId = InMemoryStorageKeys.Normalize(friendSystemId);

        if (!TryGetFriendshipState(normalizedSystemId, normalizedFriendId, out var state))
        {
            return Task.FromResult(false);
        }

        state.Level = trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend;
        return Task.FromResult(true);
    }

    public Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);

        var outgoing = _outgoingRequests.TryGetValue(normalizedSystemId, out var outStore)
            ? outStore.Values
                .OrderByDescending(r => r.DateSent)
                .Select(r => new FriendRequestReadModel(
                    new FriendProfileReadModel(r.OtherSystemId, null, null, null, null, null),
                    new FriendshipRequestModel(r.DateSent)))
                .ToList()
            : new List<FriendRequestReadModel>();

        var incoming = _outgoingRequests
            .SelectMany(kvp => kvp.Value.Values.Select(r => (From: kvp.Key, Request: r)))
            .Where(x => x.Request.OtherSystemId == normalizedSystemId)
            .OrderByDescending(x => x.Request.DateSent)
            .Select(x => new FriendRequestReadModel(
                new FriendProfileReadModel(x.From, null, null, null, null, null),
                new FriendshipRequestModel(x.Request.DateSent)))
            .ToList();

        return Task.FromResult(new FriendRequestIndexReadModel(incoming, outgoing));
    }

    public Task<SendFriendRequestOutcome> SendRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedTargetId = InMemoryStorageKeys.Normalize(targetSystemId);

        if (IsFriends(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadyFriends);
        }

        if (HasOutgoingRequest(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(SendFriendRequestOutcome.AlreadySent);
        }

        if (HasOutgoingRequest(normalizedTargetId, normalizedSystemId))
        {
            LinkFriends(normalizedSystemId, normalizedTargetId);
            RemoveRequest(normalizedTargetId, normalizedSystemId);
            RemoveRequest(normalizedSystemId, normalizedTargetId);
            return Task.FromResult(SendFriendRequestOutcome.Accepted);
        }

        var store = _outgoingRequests.GetOrAdd(normalizedSystemId, _ => new ConcurrentDictionary<SystemId, RequestState>());
        store[normalizedTargetId] = new RequestState
        {
            OtherSystemId = normalizedTargetId,
            DateSent = DateTimeOffset.UtcNow
        };

        return Task.FromResult(SendFriendRequestOutcome.Sent);
    }

    public Task<FriendRequestMutationOutcome> AcceptRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedSourceId = InMemoryStorageKeys.Normalize(sourceSystemId);

        if (IsFriends(normalizedSystemId, normalizedSourceId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSourceId, normalizedSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        LinkFriends(normalizedSystemId, normalizedSourceId);
        RemoveRequest(normalizedSourceId, normalizedSystemId);
        RemoveRequest(normalizedSystemId, normalizedSourceId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> RejectRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedSourceId = InMemoryStorageKeys.Normalize(sourceSystemId);

        if (IsFriends(normalizedSystemId, normalizedSourceId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSourceId, normalizedSystemId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(normalizedSourceId, normalizedSystemId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<FriendRequestMutationOutcome> CancelRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var normalizedTargetId = InMemoryStorageKeys.Normalize(targetSystemId);

        if (IsFriends(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.AlreadyFriends);
        }

        if (!HasOutgoingRequest(normalizedSystemId, normalizedTargetId))
        {
            return Task.FromResult(FriendRequestMutationOutcome.NotRequested);
        }

        RemoveRequest(normalizedSystemId, normalizedTargetId);
        return Task.FromResult(FriendRequestMutationOutcome.Ok);
    }

    public Task<IReadOnlyList<SystemId>> DeleteAllForSystemAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = InMemoryStorageKeys.Normalize(systemId);
        var friendIds = new List<SystemId>();

        // Remove all friendships where this user is involved
        if (_friendships.TryRemove(normalizedSystemId, out var friends))
        {
            foreach (var friendId in friends.Keys)
            {
                friendIds.Add(friendId);
                if (_friendships.TryGetValue(friendId, out var peerStore))
                {
                    peerStore.TryRemove(normalizedSystemId, out _);
                }
            }
        }

        // Remove all outgoing requests from this user
        _outgoingRequests.TryRemove(normalizedSystemId, out _);

        // Remove all incoming requests to this user (we need to scan for this in-memory)
        foreach (var requesterId in _outgoingRequests.Keys)
        {
            if (_outgoingRequests.TryGetValue(requesterId, out var requests))
            {
                requests.TryRemove(normalizedSystemId, out _);
            }
        }

        return Task.FromResult<IReadOnlyList<SystemId>>(friendIds.ToArray());
    }

    private bool IsFriends(SystemId systemId, SystemId friendSystemId)
        => TryGetFriendStore(systemId, out var store) && store.ContainsKey(friendSystemId);

    private bool HasOutgoingRequest(SystemId fromSystemId, SystemId toSystemId)
        => _outgoingRequests.TryGetValue(fromSystemId, out var store) && store.ContainsKey(toSystemId);

    private void RemoveRequest(SystemId fromSystemId, SystemId toSystemId)
    {
        if (_outgoingRequests.TryGetValue(fromSystemId, out var store))
        {
            store.TryRemove(toSystemId, out _);
        }
    }

    private void LinkFriends(SystemId left, SystemId right)
    {
        var now = DateTimeOffset.UtcNow;

        var leftStore = _friendships.GetOrAdd(left, _ => new ConcurrentDictionary<SystemId, FriendshipState>());
        leftStore[right] = new FriendshipState
        {
            FriendSystemId = right,
            Level = FriendshipLevel.Friend,
            Since = now
        };

        var rightStore = _friendships.GetOrAdd(right, _ => new ConcurrentDictionary<SystemId, FriendshipState>());
        rightStore[left] = new FriendshipState
        {
            FriendSystemId = left,
            Level = FriendshipLevel.Friend,
            Since = now
        };
    }

    private bool TryGetFriendStore(SystemId systemId, out ConcurrentDictionary<SystemId, FriendshipState> store)
        => _friendships.TryGetValue(systemId, out store!);

    private bool TryGetFriendshipState(SystemId systemId, SystemId friendSystemId, out FriendshipState state)
    {
        if (TryGetFriendStore(systemId, out var store) && store.TryGetValue(friendSystemId, out state!))
        {
            return true;
        }

        state = null!;
        return false;
    }
}


