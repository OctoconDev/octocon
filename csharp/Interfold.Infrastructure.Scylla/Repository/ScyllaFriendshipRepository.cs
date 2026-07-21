using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaFriendshipRepository : IFriendshipRepository
{
    // Derived from the ScyllaKeyspace enum so the region list can't drift from the typed
    // vocabulary the resolution APIs use.
    private static readonly string[] CanonicalRegions =
        Enum.GetValues<ScyllaKeyspace>().Select(k => k.ToWire()).ToArray();

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly IScyllaScopeResolver _scopeResolver;

    public ScyllaFriendshipRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IScyllaScopeResolver scopeResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
        _scopeResolver = scopeResolver;
    }

    public async Task<SystemId?> ResolveUserIdAsync(FriendLookup lookup, CancellationToken cancellationToken = default)
    {
        // FriendLookup guarantees the caller-supplied shape is either Kind.Id or
        // Kind.Username at this point — the wire boundary already rejected every other
        // shape with a 400. Pass the raw wire (OriginalValue via the implicit widen) into
        // ResolveUserIdInScyllaAsync so its shared re-parse picks the right registry lane
        // for both this public path and the internal defensive re-resolutions further
        // down.
        return await _scopeResolver.ExecuteGlobalAsync<SystemId?>(async scope =>
        {
            var session = scope.Session;
            return await ResolveUserIdInScyllaAsync(session, lookup, cancellationToken);
        }, cancellationToken);
    }

    public async Task<FriendshipLevel?> GetFriendshipLevelAsync(SystemId systemId, SystemId? viewerSystemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<FriendshipLevel?>(async scope =>
        {
            if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
            {
                return null;
            }

            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedViewerSystemId = _keyspaceResolver.NormalizeSystemId(viewerSystemId.Value);

            if (normalizedSystemId == normalizedViewerSystemId)
            {
                return FriendshipLevel.TrustedFriend;
            }

            var session = scope.Session;
            var query = new SimpleStatement(
                $"SELECT level FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
                normalizedSystemId,
                normalizedViewerSystemId);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : (FriendshipLevel?)row.GetValue<short>("level").FromCode<FriendshipLevel>();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<IReadOnlyList<FriendshipReadModel>>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var profileHydrationConcurrency = _options.HydrationMaxConcurrency;

            var query = new SimpleStatement(
                $"SELECT friend_id, level, since FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ?",
                normalizedSystemId);

            var rows = await session.ExecuteAsync(query);
            var result = await ConcurrentProjection.SelectWithConcurrencyAsync(
                rows,
                profileHydrationConcurrency,
                async row =>
                {
                    SystemId friendId = new(row.GetValue<string>("friend_id"));
                    var level = row.GetValue<short>("level").FromCode<FriendshipLevel>();
                    var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;

                    var profileTask = GetFriendProfileAsync(session, friendId);
                    var frontingTask = GetFrontingAsync(session, friendId, new(normalizedSystemId));
                    await Task.WhenAll(profileTask, frontingTask);

                    return new FriendshipReadModel(
                        await profileTask,
                        new FriendshipModel(level, since),
                        await frontingTask);
                },
                cancellationToken);

            return result.OrderByDescending(x => x.Friendship.Since).ToList();
        }, cancellationToken);
    }

    public async Task<FriendshipReadModel?> GetFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<FriendshipReadModel?>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedFriendSystemId = _keyspaceResolver.NormalizeSystemId(friendSystemId);

            var query = new SimpleStatement(
                $"SELECT friend_id, level, since FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
                normalizedSystemId,
                normalizedFriendSystemId);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;
            var level = row.GetValue<short>("level").FromCode<FriendshipLevel>();
            var profile = await GetFriendProfileAsync(session, new(normalizedFriendSystemId));
            var fronting = await GetFrontingAsync(session, new(normalizedFriendSystemId), new(normalizedSystemId));

            return new FriendshipReadModel(
                profile,
                new FriendshipModel(level, since),
                fronting);
        }, cancellationToken);
    }

    public async Task<bool> RemoveFriendshipAsync(SystemId systemId, SystemId friendSystemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<bool>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedFriendId = _keyspaceResolver.NormalizeSystemId(friendSystemId);

            var exists = await ExistsFriendshipAsync(session, normalizedSystemId, normalizedFriendId);
            if (!exists)
            {
                return false;
            }

            var removeBatch = new BatchStatement();
            ScyllaFriendshipDenormalizedTable.AddDeleteStatements(removeBatch, normalizedSystemId, normalizedFriendId);
            await session.ExecuteAsync(removeBatch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetTrustedAsync(SystemId systemId, SystemId friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<bool>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedFriendSystemId = _keyspaceResolver.NormalizeSystemId(friendSystemId);

            var exists = await ExistsFriendshipAsync(session, normalizedSystemId, normalizedFriendSystemId);
            if (!exists)
            {
                return false;
            }

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"UPDATE {ScyllaGlobalKeyspace.Name}.friendships SET level = ? WHERE user_id = ? AND friend_id = ?",
                (short)(trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend),
                normalizedSystemId,
                normalizedFriendSystemId));
            batch.Add(new SimpleStatement(
                $"UPDATE {ScyllaGlobalKeyspace.Name}.friendships_by_friend_id SET level = ? WHERE friend_id = ? AND user_id = ?",
                (short)(trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend),
                normalizedFriendSystemId,
                normalizedSystemId));
            await session.ExecuteAsync(batch);

            return true;
        }, cancellationToken);
    }

    public async Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<FriendRequestIndexReadModel>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var profileHydrationConcurrency = _options.HydrationMaxConcurrency;

            var incomingTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT from_id, date_sent FROM {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id WHERE to_id = ?",
                normalizedSystemId));

            var outgoingTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT to_id, date_sent FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ?",
                normalizedSystemId));

            await Task.WhenAll(incomingTask, outgoingTask);

            var incomingRows = await incomingTask;
            var outgoingRows = await outgoingTask;

            var incoming = await ConcurrentProjection.SelectWithConcurrencyAsync(
                incomingRows,
                profileHydrationConcurrency,
                async row =>
                {
                    SystemId sourceSystemId = new(row.GetValue<string>("from_id"));
                    var profile = await GetFriendProfileAsync(session, sourceSystemId);
                    return new FriendRequestReadModel(profile, new FriendshipRequestModel(row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
                },
                cancellationToken);

            var outgoing = await ConcurrentProjection.SelectWithConcurrencyAsync(
                outgoingRows,
                profileHydrationConcurrency,
                async row =>
                {
                    SystemId targetSystemId = new(row.GetValue<string>("to_id"));
                    var profile = await GetFriendProfileAsync(session, targetSystemId);
                    return new FriendRequestReadModel(profile, new FriendshipRequestModel(row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
                },
                cancellationToken);

            return new FriendRequestIndexReadModel(
                incoming.OrderByDescending(x => x.Request.DateSent).ToList(),
                outgoing.OrderByDescending(x => x.Request.DateSent).ToList());
        }, cancellationToken);
    }

    public async Task<SendFriendRequestOutcome> SendRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<SendFriendRequestOutcome>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedTargetSystemId = _keyspaceResolver.NormalizeSystemId(targetSystemId);

            if (await ResolveUserIdInScyllaAsync(session, normalizedTargetSystemId, cancellationToken) is not { } resolvedTargetUserId)
            {
                return SendFriendRequestOutcome.NoUser;
            }
            normalizedTargetSystemId = resolvedTargetUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return SendFriendRequestOutcome.AlreadyFriends;
            }

            if (await ExistsRequestAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return SendFriendRequestOutcome.AlreadySent;
            }

            if (await ExistsRequestAsync(session, normalizedTargetSystemId, normalizedSystemId))
            {
                await LinkFriendsAndClearRequestsAsync(session, normalizedSystemId, normalizedTargetSystemId);
                return SendFriendRequestOutcome.Accepted;
            }

            await CreateRequestAsync(session, normalizedSystemId, normalizedTargetSystemId);
            return SendFriendRequestOutcome.Sent;
        }, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> AcceptRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<FriendRequestMutationOutcome>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedSourceSystemId = _keyspaceResolver.NormalizeSystemId(sourceSystemId);

            if (await ResolveUserIdInScyllaAsync(session, normalizedSourceSystemId, cancellationToken) is not { } resolvedSourceUserId)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedSourceSystemId = resolvedSourceUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSourceSystemId, normalizedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await LinkFriendsAndClearRequestsAsync(session, normalizedSystemId, normalizedSourceSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> RejectRequestAsync(SystemId systemId, SystemId sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<FriendRequestMutationOutcome>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedSourceSystemId = _keyspaceResolver.NormalizeSystemId(sourceSystemId);

            if (await ResolveUserIdInScyllaAsync(session, normalizedSourceSystemId, cancellationToken) is not { } resolvedSourceUserId)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedSourceSystemId = resolvedSourceUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSourceSystemId, normalizedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, normalizedSourceSystemId, normalizedSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> CancelRequestAsync(SystemId systemId, SystemId targetSystemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<FriendRequestMutationOutcome>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedTargetSystemId = _keyspaceResolver.NormalizeSystemId(targetSystemId);

            if (await ResolveUserIdInScyllaAsync(session, normalizedTargetSystemId, cancellationToken) is not { } resolvedTargetUserId)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedTargetSystemId = resolvedTargetUserId.Value;

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, normalizedSystemId, normalizedTargetSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SystemId>> DeleteAllForSystemAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteGlobalAsync<IReadOnlyList<SystemId>>(async scope =>
        {
            var session = scope.Session;
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            // Find all friendships for this user
            var friendsTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT friend_id FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ?",
                normalizedSystemId));

            // Find all outgoing requests
            var outgoingRequestsTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT to_id FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ?",
                normalizedSystemId));

            // Find all incoming requests
            var incomingRequestsTask = session.ExecuteAsync(new SimpleStatement(
                $"SELECT from_id FROM {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id WHERE to_id = ?",
                normalizedSystemId));

            await Task.WhenAll(friendsTask, outgoingRequestsTask, incomingRequestsTask);

            var friendRows = await friendsTask;
            var outgoingRows = await outgoingRequestsTask;
            var incomingRows = await incomingRequestsTask;

            var batch = new BatchStatement();
            var friendIds = new List<SystemId>();
            foreach (var row in friendRows)
            {
                SystemId friendId = new(row.GetValue<string>("friend_id"));
                friendIds.Add(friendId);
                ScyllaFriendshipDenormalizedTable.AddDeleteStatements(batch, normalizedSystemId, friendId.Value);
            }

            foreach (var row in outgoingRows)
            {
                var targetId = row.GetValue<string>("to_id");
                batch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ? AND to_id = ?", normalizedSystemId, targetId));
                batch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?", targetId, normalizedSystemId));
            }

            foreach (var row in incomingRows)
            {
                var sourceId = row.GetValue<string>("from_id");
                batch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ? AND to_id = ?", sourceId, normalizedSystemId));
                batch.Add(new SimpleStatement($"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?", normalizedSystemId, sourceId));
            }

            if (!batch.IsEmpty)
            {
                await session.ExecuteAsync(batch);
            }

            return (IReadOnlyList<SystemId>)friendIds.ToArray();
        }, cancellationToken);
    }


    private static Task<bool> ExistsFriendshipAsync(ISession session, string userId, string friendId)
        => ScyllaExistsQueries.RowExistsAsync(session, ScyllaGlobalKeyspace.Name, "friendships", "friend_id", userId, friendId);

    private async Task<SystemId?> ResolveUserIdInScyllaAsync(
        ISession session,
        string input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim();

        // FriendLookup picks the routing lane at the friend-request wire boundary; the
        // internal defensive re-resolutions in Send/Accept/Reject/Cancel below pass a
        // post-normalization bare SystemId string which parses as Kind.Id here. An
        // unparseable input (a rare shape that slipped past both route binding and
        // NormalizeSystemId — should not happen in practice) falls through to the
        // user_registry.user_id lookup with the WHOLE input, matching
        // ScyllaUserRegistryRegionContext's fallback branch and keeping the read-side
        // behaviour identical to the pre-merge shape.
        if (!FriendLookup.TryParse(input, provider: null, out var handle))
        {
            return await LookupByUserIdAsync(session, input);
        }

        return handle.Kind switch
        {
            // Kind.Username → per-region users_by_username fanout with the after-prefix
            // Value ("alice" from "username:alice").
            FriendLookupKind.Username => await LookupByUsernameFanoutAsync(session, handle.Value),
            // Kind.Id (bare or "id:"-prefixed) → user_registry.user_id lookup with the
            // after-prefix Value.
            FriendLookupKind.Id => await LookupByUserIdAsync(session, handle.Value),
            // Only Username/Id exist today. If a new FriendLookupKind is added (e.g. Discord,
            // Region-scoped) it needs its own routing lane — silently falling through to
            // user_registry.user_id would mis-route the lookup and produce phantom nulls or
            // wrong hits. Throw so the omission is impossible to miss.
            _ => throw new ArgumentOutOfRangeException(nameof(handle), handle.Kind,
                $"Unhandled FriendLookupKind '{handle.Kind}' in ResolveUserIdInScyllaAsync."),
        };
    }

    // Internal helper: forward a plain string to the private inner resolver. Used by the
    // Send/Accept/Reject/Cancel defensive re-resolution paths that receive a normalized
    // SystemId string rather than a FriendLookup.
    private Task<SystemId?> ResolveUserIdInScyllaAsync(
        ISession session,
        FriendLookup lookup,
        CancellationToken cancellationToken)
        => ResolveUserIdInScyllaAsync(session, lookup.OriginalValue, cancellationToken);

    private static async Task<SystemId?> LookupByUserIdAsync(ISession session, string userId)
    {
        var directQuery = new SimpleStatement(
            $"SELECT user_id FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE user_id = ? LIMIT 1",
            userId);
        var directRow = (await session.ExecuteAsync(directQuery)).FirstOrDefault();
        return directRow is null
            ? null
            : new SystemId(directRow.GetValue<string>("user_id"));
    }

    private static async Task<SystemId?> LookupByUsernameFanoutAsync(ISession session, string username)
    {
        var existingKeyspaces = await GetExistingRegionalKeyspacesAsync(session);

        // Username lookup fans out across the regional keyspaces that actually exist —
        // the users_by_username table is per-region and there is no global reverse
        // index. Missing keyspaces / missing tables are skipped rather than propagated
        // so a partially-provisioned cluster still resolves usernames in the regions it
        // does have.
        foreach (var region in existingKeyspaces.Where(CanonicalRegions.Contains))
        {
            try
            {
                var userQuery = new SimpleStatement(
                    $"SELECT user_id FROM {region}.users_by_username WHERE username = ? LIMIT 1",
                    username);
                var userRow = (await session.ExecuteAsync(userQuery)).FirstOrDefault();
                if (userRow != null)
                {
                    return new(userRow.GetValue<string>("user_id"));
                }
            }
            catch (UnavailableException)
            {
                // Region keyspace/table temporarily unavailable; skip and try next region
                continue;
            }
            catch (InvalidQueryException)
            {
                // Table doesn't exist in this keyspace; skip and try next region
                continue;
            }
        }

        return null;
    }

    private static async Task<HashSet<string>> GetExistingRegionalKeyspacesAsync(ISession session)
    {
        var keyspacesQuery = new SimpleStatement("SELECT keyspace_name FROM system_schema.keyspaces");
        var keyspaceRows = await session.ExecuteAsync(keyspacesQuery);

        return keyspaceRows
            .Select(row => row.GetValue<string>("keyspace_name").ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> ExistsRequestAsync(ISession session, string fromId, string toId)
    {
        var query = new SimpleStatement(
            $"SELECT to_id FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ? AND to_id = ? LIMIT 1",
            fromId,
            toId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task CreateRequestAsync(ISession session, string fromId, string toId)
    {
        var batch = new BatchStatement();
        batch.Add(new SimpleStatement(
            $"INSERT INTO {ScyllaGlobalKeyspace.Name}.friend_requests (from_id, to_id, date_sent, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            fromId, toId));
        batch.Add(new SimpleStatement(
            $"INSERT INTO {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id (to_id, from_id, date_sent, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            toId, fromId));
        await session.ExecuteAsync(batch);
    }

    private static async Task DeleteRequestAsync(ISession session, string fromId, string toId)
    {
        var batch = new BatchStatement();
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ? AND to_id = ?",
            fromId, toId));
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?",
            toId, fromId));
        await session.ExecuteAsync(batch);
    }

    private static async Task LinkFriendsAndClearRequestsAsync(ISession session, string systemId, string otherSystemId)
    {
        var friendLevel = (short)FriendshipLevel.Friend;
        var batch = new BatchStatement();
        ScyllaFriendshipDenormalizedTable.AddInsertStatements(batch, systemId, otherSystemId, friendLevel);
        // Clear requests in both directions
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ? AND to_id = ?",
            systemId, otherSystemId));
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests WHERE from_id = ? AND to_id = ?",
            otherSystemId, systemId));
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?",
            otherSystemId, systemId));
        batch.Add(new SimpleStatement(
            $"DELETE FROM {ScyllaGlobalKeyspace.Name}.friend_requests_by_to_id WHERE to_id = ? AND from_id = ?",
            systemId, otherSystemId));
        await session.ExecuteAsync(batch);
    }

    private async Task<FriendProfileReadModel> GetFriendProfileAsync(ISession session, SystemId friendSystemId)
    {
        var resolvedRegion = await ResolveUserRegionAsync(session, friendSystemId);
        if (resolvedRegion is not { } typedRegion)
        {
            return new FriendProfileReadModel(friendSystemId, null, null, null, null, null);
        }

        var region = typedRegion.ToWire();
        var profileQuery = new SimpleStatement(
            $"SELECT username, avatar_url, avatar_source, description, discord_id FROM {region}.users WHERE id = ? LIMIT 1",
            friendSystemId.Value);

        var profileRow = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();

        return new FriendProfileReadModel(
            friendSystemId,
            profileRow?.GetValue<string?>("username") is { } username ? new Username(username) : null,
            AvatarUrl.FromNullable(profileRow?.GetValue<string?>("avatar_url")),
            profileRow is not null ? profileRow.GetValue<short?>("avatar_source").FromCodeOrNull<AvatarSource>() : null,
            profileRow?.GetValue<string?>("description"),
            profileRow?.GetValue<string?>("discord_id") is { } discordId ? new DiscordId(discordId) : null);
    }

    private async Task<IReadOnlyList<FriendFrontingReadModel>> GetFrontingAsync(ISession session, SystemId friendSystemId, SystemId viewerSystemId)
    {
        var resolvedRegion = await ResolveUserRegionAsync(session, friendSystemId);
        if (resolvedRegion is not { } typedRegion)
        {
            return [];
        }

        var regionalKeyspace = typedRegion.ToWire();

        // Friendship level from the friend's perspective (they control their own alter visibility)
        var levelTask = session.ExecuteAsync(new SimpleStatement(
            $"SELECT level FROM {ScyllaGlobalKeyspace.Name}.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
            friendSystemId.Value,
            viewerSystemId.Value));
        var activeTask = session.ExecuteAsync(new SimpleStatement(
            $"SELECT alter_id, comment FROM {regionalKeyspace}.current_fronts WHERE user_id = ?",
            friendSystemId.Value));

        var altersTask = session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, name, avatar_url, avatar_source, pronouns, color, description, extra_images, security_level FROM {regionalKeyspace}.alters WHERE user_id = ?",
            friendSystemId.Value));

        await Task.WhenAll(levelTask, activeTask, altersTask);

        var levelRow = (await levelTask).FirstOrDefault();
        FriendshipLevel? friendshipLevel = levelRow is null ? null : levelRow.GetValue<short>("level").FromCode<FriendshipLevel>();
        var activeRows = await activeTask;
        var primaryAlterId = await ScyllaSharedQueries.LoadPrimaryFrontAlterAsync(session, regionalKeyspace, friendSystemId.Value);
        var alterRows = await altersTask;

        var alterMap = alterRows
            .Where(row => CanViewAlter(friendshipLevel, row.GetValue<short?>("security_level")))
            .ToDictionary(
                row => row.GetValue<short>("id"),
                row => (
                    Name: row.GetValue<string?>("name"),
                    AvatarUrl: AvatarUrl.FromNullable(row.GetValue<string?>("avatar_url")),
                    AvatarSource: row.GetValue<short?>("avatar_source").FromCodeOrNull<AvatarSource>(),
                    Pronouns: row.GetValue<string?>("pronouns"),
                    Color: HexColor.FromNullable(row.GetValue<string?>("color")),
                    Description: row.GetValue<string?>("description"),
                    ExtraImages: (IReadOnlyList<AvatarUrl>)(row.GetValue<IEnumerable<string>?>("extra_images")?.Select(url => new AvatarUrl(url)).ToList() ?? [])));

        return activeRows
            .Select(row =>
            {
                var alterIdShort = row.GetValue<short>("alter_id");
                var alterId = new AlterId(alterIdShort);
                if (!alterMap.TryGetValue(alterIdShort, out var alter))
                {
                    return null;
                }
                return new FriendFrontingReadModel(
                    new FriendFrontingAlterReadModel(
                        alterId,
                        alter.Name,
                        alter.Pronouns,
                        alter.Description,
                        [],
                        alter.AvatarUrl,
                        alter.AvatarSource,
                        alter.ExtraImages,
                        alter.Color),
                    new FriendFrontingFrontReadModel(alterId, row.GetValue<string?>("comment")),
                    primaryAlterId == alterId);
            })
            .Where(x => x is not null)
            .OrderBy(x => x!.Alter.Id.Value)
            .ToList()!;
    }

    private static bool CanViewAlter(FriendshipLevel? friendshipLevel, short? securityLevel)
        => securityLevel.FromCode<VisibilityLevel>().CanBeViewedBy(friendshipLevel);

    // Returns null when the registry row is absent or carries an unknown region value —
    // callers treat both as "profile unavailable" rather than guessing a keyspace.
    private static async Task<ScyllaKeyspace?> ResolveUserRegionAsync(ISession session, SystemId userId)
    {
        var regionQuery = new SimpleStatement(
            $"SELECT region FROM {ScyllaGlobalKeyspace.Name}.user_registry WHERE user_id = ? LIMIT 1",
            userId.Value);

        var row = (await session.ExecuteAsync(regionQuery)).FirstOrDefault();
        var raw = row?.GetValue<string>("region");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return EnumWireExtensions.ParseScyllaKeyspace(raw);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
