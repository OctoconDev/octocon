using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Settings.Contracts.Events;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Socket.Api.Models;
using Interfold.Socket.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Interfold.Socket.IntegrationTests.Endpoints;

// 5 minute timeout since we want to ensure this does end up timing out if a connection gets stuck but we also 
// need to account for Cassandra's slower performance with bootstrapping.
[Timeout(1000 * 300)]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class WebSocketTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    internal static string UniqueId(string prefix) => TestIds.NewSystemId(prefix, maxLen: int.MaxValue);

    // Events carry ScopedSystemId, so tests that publish directly onto the bus compose
    // one from the raw test id. NAM is the only region the test bootstrapper seeds;
    // compose is idempotent, so callers that already pass "nam:..." (or another valid
    // region prefix) keep the same wire bytes.
    internal static ScopedSystemId AsScopedSystemId(string rawSystemId)
        => ScopedSystemId.Compose(ScyllaKeyspace.Nam, rawSystemId);

    [Test]
    public async Task Api_UserSocketEndpoint_AllowsWebSocketUpgrade(CancellationToken token)
    {
        var server = fixture.Factory.Server;
        var client = server.CreateWebSocketClient();
        
        var systemId = UniqueId("sys-phx-join");
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(server), "/api/socket/websocket?token=" + socketToken);
        
        using var ws = await client.ConnectAsync(uri, token);

        await Assert.That(ws.State).IsEqualTo(WebSocketState.Open).Because($"Expected websocket to be open after connecting to /api/socket/weboscket, got {ws.State}.");

        // Raw dictionary payload (not the typed PhxJoinPayload): pins the server's tolerance
        // for unknown platform spellings ("wasm") which the typed record can no longer emit.
        var arrayJoinFrame = PhxArrayFrame.CreateBytes(
            "51", "51", $"system:{systemId}", "phx_join",
            new Dictionary<string, object?>
            {
                ["token"] = socketToken,
                ["protocolVersion"] = "2.0.0",
                ["platform"] = "wasm",
                ["isReconnect"] = true,
            });

        await ws.SendTextFrameAsync(arrayJoinFrame, token);

        var frame = await ReceivedPhxFrame.ReceiveAsync(ws, token);

        using (Assert.Multiple())
        {
            await Assert.That(frame.JoinRef).IsEqualTo("51").Because("Expected join_ref=51 in array reply.");
            await Assert.That(frame.Ref).IsEqualTo("51").Because("Expected ref=51 in array reply.");
            await Assert.That(frame.Topic).IsEqualTo($"system:{systemId}").Because("Expected topic to match array join topic.");
            await Assert.That(frame.Event).IsEqualTo("phx_reply").Because("Expected array reply event phx_reply.");
            var reply = frame.Reply<SocketJoinReconnectPayload>();
            await Assert.That(reply.Status).IsEqualTo(PhoenixReplyStatus.Ok).Because("Expected status=ok for reconnect join.");
            await Assert.That(reply.Response.System.Id).IsEqualTo(new SystemId(systemId)).Because("Expected system ID in reconnect payload.");
        }
    }
    
    [Test]
    public async Task Api_UserSocketEndpoint_RejectsUnsupportedProtocolVersion(CancellationToken token)
    {
        var systemId = UniqueId("sys-phx-unsupported");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = $"system:{systemId}",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = new SocketToken(socketToken), ProtocolVersion = "not-a-version" },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendTextFrameAsync(joinFrame, token);
        var reply = await ReceivedPhxFrame.ReceiveReplyAsync<SocketReasonResponse>(ws, token);

        using (Assert.Multiple())
        {
            await Assert.That(reply.Status).IsEqualTo(PhoenixReplyStatus.Error).Because("Expected status=error for unsupported protocol version.");
            await Assert.That(reply.Response.Reason).IsEqualTo(ErrorCodes.SocketReasons.UnsupportedProtocolVersion).Because("Expected reason=unsupported_protocol_version.");
        }

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_BatchesForIos_WhenThresholdExceeded(CancellationToken token)
    {
        var systemId = UniqueId("sys-phx-ios-batch");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = $"system:{systemId}",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = new SocketToken(socketToken), Platform = ClientPlatform.Ios, ProtocolVersion = "2.0.0", ForceBatch = true },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendTextFrameAsync(joinFrame, token);
        var reply = await ReceivedPhxFrame.ReceiveReplyAsync<SocketJoinBatchedPayload>(ws, token);

        using (Assert.Multiple())
        {
            await Assert.That(reply.Status).IsEqualTo(PhoenixReplyStatus.Ok).Because("Expected status=ok for iOS batched join.");
            await Assert.That(reply.Response.Batched).IsTrue().Because("Expected batched=true for iOS join above threshold.");
        }

        var batchedComplete = await ws.ReceiveEventFrameAsync( token, SocketEventNames.BatchedInit.Complete, maxFrames: 6);
        await Assert.That(batchedComplete).IsNotNull().Because("Expected batched_init_complete after iOS batched join.");

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_RateLimitsThirdJoinWithinOneSecond(CancellationToken token)
    {
        var systemId = UniqueId("sys-rate-limit");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var firstReply = await SendJoinAndReceiveReplyAsync(ws, socketToken, systemId, "1", token);
        var secondReply = await SendJoinAndReceiveReplyAsync(ws, socketToken, systemId, "2", token);
        var thirdReply = await SendJoinAndReceiveReplyAsync(ws, socketToken, systemId, "3", token);

        using (Assert.Multiple())
        {
            await Assert.That(firstReply.Status).IsEqualTo(PhoenixReplyStatus.Ok).Because("Expected first join to be accepted.");
            await Assert.That(secondReply.Status).IsEqualTo(PhoenixReplyStatus.Ok).Because("Expected second join to be accepted.");
            await Assert.That(thirdReply.Status).IsEqualTo(PhoenixReplyStatus.Error).Because("Expected third join to be rate-limited.");
        }

        var thirdResponse = JsonSerializer.Deserialize<SocketReasonResponse>(
            JsonSerializer.Serialize(thirdReply.Response, SocketJson.Options), SocketJson.Options);
        await Assert.That(thirdResponse!.Reason).IsEqualTo(ErrorCodes.SocketReasons.RateLimited).Because("Expected reason=rate_limited on third join.");

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFrontingChangedEvent_AfterFrontStart(CancellationToken token)
    {
        var systemId = UniqueId("sys-front-push");
        var topic = $"system:{systemId}";
        var (rawWs, socketToken) = await WebSocketHarness.ConnectAndJoinAsync(fixture, systemId, token);
        using var ws = rawWs;

        var createAlterFrame = PhxEndpointFrame.Build(topic, "POST", "/api/systems/me/alters", new { name = "FrontPushAlter" }, "2");

        var (createAlterReply, createAlterPush) = await ws.SendEndpointAndCaptureAsync(createAlterFrame, SocketEventNames.Alters.Created, token);

        await Assert.That(createAlterReply).IsNotNull().Because("Expected endpoint ack (phx_reply) for alter create call.");

        var createdAlterId = createAlterPush is not null
            ? createAlterPush.RawPayload!.Value.GetProperty("alter").GetProperty("id").GetInt32()
            : ExtractAlterIdFromEndpointReply(createAlterReply!);

        var startFrontFrame = PhxEndpointFrame.Build(topic, "POST", "/api/systems/me/front/start", new { id = createdAlterId }, "3");

        var (endpointAck, frontingPush) = await ws.SendEndpointAndCaptureAsync(startFrontFrame, SocketEventNames.Fronting.Started, token);

        using (Assert.Multiple())
        {
            await Assert.That(endpointAck).IsNotNull().Because("Expected endpoint ack (phx_reply) for front start call.");
        }

        if (frontingPush is not null)
        {
            await Assert.That(frontingPush.RawPayload?.TryGetProperty("front", out _) ?? false)
                .IsTrue().Because("Expected fronting push payload to include front object.");
        }

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesAlterTagAndFieldsEvents_AfterEndpointWrites(CancellationToken token)
    {
        var systemId = UniqueId("sys-domain-fanout");
        var topic = $"system:{systemId}";
        var (rawWs, socketToken) = await WebSocketHarness.ConnectAndJoinAsync(fixture, systemId, token);
        using var ws = rawWs;

        var createAlterFrame = PhxEndpointFrame.Build(topic, "POST", "/api/systems/me/alters", new { name = "DomainFanoutAlter" }, "2");

        var (alterReply, alterPush) = await ws.SendEndpointAndCaptureAsync( createAlterFrame, SocketEventNames.Alters.Created, token);
        using (Assert.Multiple())
        {
            await Assert.That(alterReply).IsNotNull().Because("Expected endpoint ack for alter create.");
            await Assert.That(alterPush).IsNotNull().Because("Expected alter_created push after alter create.");
        }
        await Assert.That(alterPush!.RawPayload?.GetProperty("alter").GetProperty("name").GetString())
            .IsEqualTo("DomainFanoutAlter").Because("Expected alter name in push payload.");

        var createTagFrame = PhxEndpointFrame.Build(topic, "POST", "/api/systems/me/tags", new { name = "DomainFanoutTag" }, "3");

        var (tagReply, tagPush) = await ws.SendEndpointAndCaptureAsync( createTagFrame, SocketEventNames.Tags.Created, token);
        using (Assert.Multiple())
        {
            await Assert.That(tagReply).IsNotNull().Because("Expected endpoint ack for tag create.");
            await Assert.That(tagPush).IsNotNull().Because("Expected tag_created push after tag create.");
        }
        await Assert.That(tagPush!.RawPayload?.GetProperty("tag").GetProperty("name").GetString())
            .IsEqualTo("DomainFanoutTag").Because("Expected tag name in push payload.");

        var createFieldFrame = PhxEndpointFrame.Build(topic, "POST", "/api/settings/fields", new { name = "DomainFanoutField", type = "text", security_level = "private", locked = false }, "4");

        var (fieldReply, fieldPush) = await ws.SendEndpointAndCaptureAsync( createFieldFrame, SocketEventNames.Settings.FieldsUpdated, token);
        using (Assert.Multiple())
        {
            await Assert.That(fieldReply).IsNotNull().Because("Expected endpoint ack for field create.");
            await Assert.That(fieldPush).IsNotNull().Because("Expected fields_updated push after field create.");
        }
        await Assert.That(fieldPush!.RawPayload!.Value.GetProperty("fields").GetArrayLength())
            .IsGreaterThan(0).Because("Expected at least one field in fields_updated payload.");

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestReceived_ToRecipientSystem(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-friend-sender");
        var recipientSystemId = UniqueId("sys-friend-recipient");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, senderSystemId, recipientSystemId, token);
        using var senderWs = pair.FirstWs;
        using var recipientWs = pair.SecondWs;

        var (senderAck, senderPush, recipientFrame) = await FriendRequestFlow.SendAndDrainAsync(
            senderWs, senderSystemId, recipientWs, recipientSystemId, "2", token);

        using (Assert.Multiple())
        {
            await Assert.That(senderAck).IsNotNull().Because("Expected endpoint ack on sender socket for friend request send.");
            await Assert.That(senderPush).IsNotNull().Because("Expected friend_request_sent push on sender socket.");
        }

        await Assert.That(recipientFrame).IsNotNull().Because("Expected recipient-side friend_request_received push.");

        await Assert.That(recipientFrame!.RawPayload?.TryGetProperty("system", out _) ?? false)
            .IsTrue().Because("Expected system profile in friend_request_received payload.");

        await WebSocketExtensions.CloseTestDoneAsync(senderWs, recipientWs, token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestAccepted_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-accept-sender");
        var recipientSystemId = UniqueId("sys-accept-recipient");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, senderSystemId, recipientSystemId, token);
        using var senderWs = pair.FirstWs;
        using var recipientWs = pair.SecondWs;

        var (_, _, recipientEvent) = await FriendRequestFlow.SendAndDrainAsync(
            senderWs, senderSystemId, recipientWs, recipientSystemId, "2", token);
        await Assert.That(recipientEvent).IsNotNull().Because("Expected friend_request_received");

        var acceptFrame = PhxEndpointFrame.Build("system:" + recipientSystemId, "POST", "/api/friend-requests/" + senderSystemId + "/accept", new object(), "3");

        var (recipientAck, recipientFriendAdded) = await recipientWs.SendEndpointAndCaptureAsync(acceptFrame, SocketEventNames.Friendships.Added, token);

        using (Assert.Multiple())
        {
            await Assert.That(recipientAck).IsNotNull().Because("Expected endpoint ack on recipient socket for accept.");
            await Assert.That(recipientFriendAdded).IsNotNull().Because("Expected friend_added push on recipient socket after accept.");
        }

        var (senderFriendAdded, senderRequestCleared) = await ReceivedPhxFrame.ReceiveTwoOfAsync(
            senderWs, SocketEventNames.Friendships.Added, SocketEventNames.Friendships.RequestRemoved,
            TimeSpan.FromSeconds(5), 5, token);

        using (Assert.Multiple())
        {
            await Assert.That(senderFriendAdded).IsNotNull().Because("Expected friend_added push on sender socket after accept.");
            await Assert.That(senderRequestCleared).IsNotNull().Because("Expected friend_request_removed push on sender socket after accept (outgoing request cleanup).");
        }

        await WebSocketExtensions.CloseTestDoneAsync(senderWs, recipientWs, token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestRejected_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-reject-sender");
        var recipientSystemId = UniqueId("sys-reject-recipient");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, senderSystemId, recipientSystemId, token);
        using var senderWs = pair.FirstWs;
        using var recipientWs = pair.SecondWs;

        var (_, _, recipientEvent) = await FriendRequestFlow.SendAndDrainAsync(
            senderWs, senderSystemId, recipientWs, recipientSystemId, "2", token);
        await Assert.That(recipientEvent).IsNotNull().Because("Expected friend_request_received");

        var rejectFrame = PhxEndpointFrame.Build("system:" + recipientSystemId, "DELETE", "/api/friend-requests/" + senderSystemId, new object(), "3");

        var (recipientAck, recipientRemoved) = await recipientWs.SendEndpointAndCaptureAsync(rejectFrame, SocketEventNames.Friendships.RequestRemoved, token);

        using (Assert.Multiple())
        {
            await Assert.That(recipientAck).IsNotNull().Because("Expected endpoint ack on recipient socket for reject.");
        }

        var senderRemoved = await senderWs.ReceiveEventFrameAsync( token, SocketEventNames.Friendships.RequestRemoved, maxFrames: 8);

        await WebSocketExtensions.CloseTestDoneAsync(senderWs, recipientWs, token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestCancelled_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-cancel-sender");
        var recipientSystemId = UniqueId("sys-cancel-recipient");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, senderSystemId, recipientSystemId, token);
        using var senderWs = pair.FirstWs;
        using var recipientWs = pair.SecondWs;

        var (_, _, recipientReceived) = await FriendRequestFlow.SendAndDrainAsync(
            senderWs, senderSystemId, recipientWs, recipientSystemId, "2", token);
        await Assert.That(recipientReceived).IsNotNull().Because("Expected friend_request_received before cancel.");

        var cancelFrame = PhxEndpointFrame.Build("system:" + senderSystemId, "DELETE", "/api/friend-requests/" + recipientSystemId, new object(), "3");

        var (senderAck, senderRemoved) = await senderWs.SendEndpointAndCaptureAsync(cancelFrame, SocketEventNames.Friendships.RequestRemoved, token);

        using (Assert.Multiple())
        {
            await Assert.That(senderAck).IsNotNull().Because("Expected endpoint ack on sender socket for cancel.");
            await Assert.That(senderRemoved).IsNotNull().Because("Expected friend_request_removed push on sender socket after cancel.");
        }

        var recipientRemoved = await recipientWs.ReceiveEventFrameAsync( token, SocketEventNames.Friendships.RequestRemoved, maxFrames: 3, perFrameTimeoutSeconds: 5);
        await Assert.That(recipientRemoved).IsNotNull().Because("Expected recipient-side friend_request_removed push.");

        await WebSocketExtensions.CloseTestDoneAsync(senderWs, recipientWs, token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRemoved_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-remove-sender");
        var recipientSystemId = UniqueId("sys-remove-recipient");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, senderSystemId, recipientSystemId, token);
        using var senderWs = pair.FirstWs;
        using var recipientWs = pair.SecondWs;

        // Send friend request
        var (_, _, recipientReceived) = await FriendRequestFlow.SendAndDrainAsync(
            senderWs, senderSystemId, recipientWs, recipientSystemId, "2", token);
        await Assert.That(recipientReceived).IsNotNull().Because("Expected friend_request_received before accept.");

        // Accept friend request
        var acceptFrame = PhxEndpointFrame.Build("system:" + recipientSystemId, "POST", "/api/friend-requests/" + senderSystemId + "/accept", new object(), "3");

        var (recipientAcceptAck, recipientAdded) = await recipientWs.SendEndpointAndCaptureAsync(acceptFrame, SocketEventNames.Friendships.Added, token);

        using (Assert.Multiple())
        {
            await Assert.That(recipientAcceptAck).IsNotNull().Because("Expected endpoint ack on recipient socket for accept.");
            await Assert.That(recipientAdded).IsNotNull().Because("Expected friend_added push on recipient socket after accept.");
        }

        // Drain sender-side accept events
        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.Added);

        // Remove friend
        var removeFrame = PhxEndpointFrame.Build("system:" + senderSystemId, "DELETE", "/api/friends/" + recipientSystemId, new object(), "4");

        var (senderRemoveAck, senderRemoved) = await senderWs.SendEndpointAndCaptureAsync(removeFrame, SocketEventNames.Friendships.Removed, token);

        using (Assert.Multiple())
        {
            await Assert.That(senderRemoveAck).IsNotNull().Because("Expected endpoint ack on sender socket for remove.");
            await Assert.That(senderRemoved).IsNotNull().Because("Expected friend_removed push on sender socket after remove.");
        }

        if (senderRemoved is not null)
        {
            await Assert.That(senderRemoved.Event).IsEqualTo(SocketEventNames.Friendships.Removed)
                .Because("Expected friend_removed event on sender socket.");
        }

        var recipientRemovedFrame = await recipientWs.ReceiveEventFrameAsync( token, SocketEventNames.Friendships.Removed, maxFrames: 8);
        await Assert.That(recipientRemovedFrame).IsNotNull().Because("Timed out waiting for recipient-side friend_removed push.");

        await WebSocketExtensions.CloseTestDoneAsync(senderWs, recipientWs, token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendTrustedAndUntrusted_ToActor(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-trust-sender");
        var recipientSystemId = UniqueId("sys-trust-recipient");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, senderSystemId, recipientSystemId, token);
        using var senderWs = pair.FirstWs;
        using var recipientWs = pair.SecondWs;

        await FriendTrustUntrustFlow.RunAsync(senderWs, senderSystemId, recipientWs, recipientSystemId, token);

        await WebSocketExtensions.CloseTestDoneAsync(senderWs, recipientWs, token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendAdded_OnMutualFriendRequest(CancellationToken token)
    {
        var systemAId = UniqueId("sys-mutual-a");
        var systemBId = UniqueId("sys-mutual-b");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, systemAId, systemBId, token);
        using var wsA = pair.FirstWs;
        using var wsB = pair.SecondWs;

        // A sends friend request to B
        var sendAFrame = PhxEndpointFrame.Build("system:" + systemAId, "PUT", "/api/friend-requests/" + systemBId, new object(), "2");

        _ = await wsA.SendEndpointAndCaptureAsync(sendAFrame, SocketEventNames.Friendships.RequestSent, token);
        _ = await wsB.ReceiveEventFrameAsync( token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3);

        // B sends mutual friend request to A (should auto-accept)
        var sendBFrame = PhxEndpointFrame.Build("system:" + systemBId, "PUT", "/api/friend-requests/" + systemAId, new object(), "3");

        await wsB.SendTextFrameAsync(sendBFrame, token);

        var (bAck, bFriendAdded) = await ReceivedPhxFrame.ReceiveTwoOfAsync(
            wsB, "phx_reply", SocketEventNames.Friendships.Added,
            TimeSpan.FromSeconds(2), 5, token);

        using (Assert.Multiple())
        {
            await Assert.That(bAck).IsNotNull().Because("Expected endpoint ack on B socket for mutual send.");
            await Assert.That(bFriendAdded).IsNotNull().Because("Expected friend_added push on B socket after mutual send auto-accept.");
        }

        var (aFriendAdded, aRequestCleared) = await ReceivedPhxFrame.ReceiveTwoOfAsync(
            wsA, SocketEventNames.Friendships.Added, SocketEventNames.Friendships.RequestRemoved,
            TimeSpan.FromSeconds(2), 5, token);

        using (Assert.Multiple())
        {
            await Assert.That(aFriendAdded).IsNotNull().Because("Expected friend_added push on A socket after mutual send auto-accept.");
            await Assert.That(aRequestCleared).IsNotNull().Because("Expected friend_request_removed push on A socket after mutual send auto-accept (outgoing request cleanup).");
        }

        await WebSocketExtensions.CloseTestDoneAsync(wsA, wsB, token);
    }
    
    [Test]
    public async Task Api_UserSocketEndpoint_PushesTagsWiped_AfterWipeTagsEndpoint(CancellationToken token)
    {
        var systemId = UniqueId("sys-tags-wiped");
        var topic = $"system:{systemId}";
        var (rawWs, socketToken) = await WebSocketHarness.ConnectAndJoinAsync(fixture, systemId, token);
        using var ws = rawWs;

        // Seed a tag so the wipe path actually iterates the repository — exercises the cascade
        // path Octocon.Accounts.wipe_tags/1 used to cover in the legacy worker.
        var createTagFrame = PhxEndpointFrame.Build(topic, "POST", "/api/systems/me/tags", new { name = "WipeMe" }, "2");

        _ = await ws.SendEndpointAndCaptureAsync( createTagFrame, SocketEventNames.Tags.Created, token);

        var wipeFrame = PhxEndpointFrame.Build(topic, "POST", "/api/settings/wipe-tags", new object(), "3");

        var (wipeAck, wipePush) = await ws.SendEndpointAndCaptureAsync( wipeFrame, SocketEventNames.Settings.TagsWiped, token);

        using (Assert.Multiple())
        {
            await Assert.That(wipeAck).IsNotNull().Because("Expected endpoint ack for wipe-tags call.");
            await Assert.That(wipePush).IsNotNull().Because("Expected tags_wiped push after POST /api/settings/wipe-tags.");
        }

        if (wipePush is not null)
        {
            await Assert.That(wipePush.Event).IsEqualTo(SocketEventNames.Settings.TagsWiped)
                .Because("Expected event name on wipe push to be tags_wiped.");
        }

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesGoogleAccountUnlinked_AfterUnlinkEmailEndpoint(CancellationToken token)
    {
        var systemId = UniqueId("sys-google-unlink");
        var topic = $"system:{systemId}";
        var (rawWs, socketToken) = await WebSocketHarness.ConnectAndJoinAsync(fixture, systemId, token);
        using var ws = rawWs;

        var unlinkFrame = PhxEndpointFrame.Build(topic, "POST", "/api/settings/unlink_email", new object(), "2");

        var (ack, push) = await ws.SendEndpointAndCaptureAsync( unlinkFrame, SocketEventNames.Settings.GoogleAccountUnlinked, token);

        using (Assert.Multiple())
        {
            await Assert.That(ack).IsNotNull().Because("Expected endpoint ack for unlink_email call.");
            await Assert.That(push).IsNotNull().Because("Expected google_account_unlinked push after POST /api/settings/unlink_email (legacy contract for the email auth path).");
        }

        if (push is not null)
        {
            await Assert.That(push.Event).IsEqualTo(SocketEventNames.Settings.GoogleAccountUnlinked)
                .Because("Expected event name on unlink push to be google_account_unlinked.");
        }

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesSpImportLifecycleEvents_OnEventBusPublish(CancellationToken token)
    {
        // The Simply Plural import service hits the live Apparyllis API, so we drive the socket
        // pump from the bus instead. Both lifecycle events are wired through SocketEventPumpRunner;
        // this test covers the projection contract (event name + alter_count payload) without
        // depending on outbound HTTP. The matching publish-from-handler paths are exercised by
        // ImportSpCommandHandler unit tests where the import service is stubbed.
        var systemId = UniqueId("sys-sp-import");
        var topic = $"system:{systemId}";
        var (rawWs, socketToken) = await WebSocketHarness.ConnectAndJoinAsync(fixture, systemId, token);
        using var ws = rawWs;

        await fixture.Factory.EventBus.PublishAsync(new SimplyPluralImportCompletedEvent(AsScopedSystemId(systemId), 7), token);

        var completeFrame = await ws.ReceiveEventFrameAsync( token, SocketEventNames.Imports.SpComplete, maxFrames: 4);
        await Assert.That(completeFrame).IsNotNull().Because("Expected sp_import_complete push after bus publish.");
        await Assert.That(completeFrame!.RawPayload?.GetProperty("alter_count").GetInt32() ?? -1)
            .IsEqualTo(7).Because("Expected alter_count=7 in sp_import_complete payload (legacy contract is snake_case).");

        await fixture.Factory.EventBus.PublishAsync(new SimplyPluralImportFailedEvent(AsScopedSystemId(systemId)), token);

        var failedFrame = await ws.ReceiveEventFrameAsync( token, SocketEventNames.Imports.SpFailed, maxFrames: 4);
        await Assert.That(failedFrame).IsNotNull().Because("Expected sp_import_failed push after bus publish.");

        await ws.CloseTestDoneAsync(token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesPkImportLifecycleEvents_OnEventBusPublish(CancellationToken token)
    {
        // PluralKit import is a TODO in the new stack, so the lifecycle wiring is verified the
        // same way as SP: drive the bus directly and assert the projection. The handler-side
        // publish path will start firing the same events for free once the importer lands.
        var systemId = UniqueId("sys-pk-import");
        var topic = $"system:{systemId}";
        var (rawWs, socketToken) = await WebSocketHarness.ConnectAndJoinAsync(fixture, systemId, token);
        using var ws = rawWs;

        await fixture.Factory.EventBus.PublishAsync(new PluralKitImportCompletedEvent(AsScopedSystemId(systemId), 3), token);

        var completeFrame = await ws.ReceiveEventFrameAsync( token, SocketEventNames.Imports.PkComplete, maxFrames: 4);
        await Assert.That(completeFrame).IsNotNull().Because("Expected pk_import_complete push after bus publish.");
        await Assert.That(completeFrame!.RawPayload?.GetProperty("alter_count").GetInt32() ?? -1)
            .IsEqualTo(3).Because("Expected alter_count=3 in pk_import_complete payload (legacy contract is snake_case).");

        await fixture.Factory.EventBus.PublishAsync(new PluralKitImportFailedEvent(AsScopedSystemId(systemId)), token);

        var failedFrame = await ws.ReceiveEventFrameAsync( token, SocketEventNames.Imports.PkFailed, maxFrames: 4);
        await Assert.That(failedFrame).IsNotNull().Because("Expected pk_import_failed push after bus publish.");

        await ws.CloseTestDoneAsync(token);
    }

    /// <summary>Regression for the docker-deployment crash: the WebSocket endpoint relay
    /// used to build its outbound URL from the inbound Host (operator-facing hostname +
    /// host-mapped port), which the container can neither resolve nor reach. Recorder
    /// asserts the proxy dials the loopback Kestrel listener, not the operator authority
    /// — TestServer routes by path so only URI-recording distinguishes the two.</summary>
    [Test, NotInParallel("websocket-uri-recorder")]
    public async Task Api_UserSocketEndpoint_ProxiesToLocalListener_RegardlessOfInboundHostHeader(CancellationToken token)
    {
        var systemId = UniqueId("sys-proxy-loopback");
        var topic = $"system:{systemId}";
        var socketToken = await CreateRandomToken(fixture.Factory, systemId);

        var recorder = new ConcurrentQueue<InterfoldWebApplicationFactory.RecordedHttpCall>();
        fixture.Factory.OutboundHttpUriRecorder = recorder;
        try
        {
            var wsClient = fixture.Factory.Server.CreateWebSocketClient();
            // Simulate the production topology: inbound Host is the operator-facing
            // hostname + host-side port; the internal Kestrel port differs (5100/5101 by
            // default) and the hostname isn't in the container's DNS. The proxy MUST
            // ignore both.
            //
            // Port must be a valid TCP port because HandleEndpointProxyAsync assigns
            // Request.Host.Value to Headers.Host, whose setter validates via
            // ParserHelpers.CheckValidHost. The real crash-report values are both valid
            // AND the most faithful reproduction of the production scenario.
            const string operatorFacingHost = "pineapple.local";
            const int hostSideMappedPort = 5001;
            wsClient.ConfigureRequest = request =>
            {
                request.Host = new HostString(operatorFacingHost, hostSideMappedPort);
            };

            var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
            using var ws = await wsClient.ConnectAsync(uri, token);

            await WebSocketExtensions.JoinTopicAsync(ws, topic, socketToken, token);

            // Known-good relay target (returns 201) keeps signal on URI composition, not
            // controller wiring. The endpoint also fires alter_created which interleaves
            // with phx_reply; SendEndpointAndCaptureAsync handles either order.
            var endpointFrame = PhxEndpointFrame.Build(topic, "POST", "/api/systems/me/alters", new { name = "LoopbackProbeAlter" }, "2");

            var (replyFrame, _) = await ws.SendEndpointAndCaptureAsync(endpointFrame, SocketEventNames.Alters.Created, token);
            await Assert.That(replyFrame).IsNotNull()
                .Because("Expected a phx_reply for the endpoint proxy call.");
            var reply = replyFrame!.Reply<SocketEndpointProxyResponse>();

            using (Assert.Multiple())
            {
                // The proxy completing successfully despite the bogus inbound Host is
                // the first half of the contract — pre-fix the call would have built a
                // non-loopback URL string, which under real Kestrel produces the
                // "Name or service not known" SocketException seen in the production
                // crash. Under TestServer the authority is cosmetic, so this assertion
                // alone is not sufficient — see the URI-recorder assertions below.
                await Assert.That(reply.Status).IsEqualTo(PhoenixReplyStatus.Ok)
                    .Because("Expected the endpoint-proxy phx_reply to be ok; the proxy must succeed regardless of the inbound Host header.");
                await Assert.That(reply.Response.Status).IsEqualTo(System.Net.HttpStatusCode.Created)
                    .Because("Expected the relayed POST /api/systems/me/alters to return 201 Created (mirrors the contract from the existing endpoint-proxy tests above).");

                // The outbound recorder assertions ARE the regression guard. Pre-fix the
                // proxy passed `{Request.Scheme}://{Request.Host}{path}` to HttpClient,
                // which the recorder would have captured as containing `pineapple.local`
                // and `:5001`. Post-fix the proxy passes the IServerAddressesFeature-
                // derived loopback URL (or the TestServer fallback `http://localhost`),
                // so neither token appears.
                var recorded = recorder.ToArray();
                await Assert.That(recorded.Length).IsGreaterThan(0)
                    .Because("Expected the endpoint proxy to issue at least one outbound HttpClient call (the relayed POST to /api/systems/me/alters).");

                // Filter to the relayed path AND our operator-facing Host header. The
                // recorder hook is process-wide, so concurrent websocket tests that relay
                // the same endpoint through the proxy (with their own default `localhost`
                // upgrade host) also land in the queue — matching on this test's unique
                // Host value picks out OUR relay deterministically. This keeps the
                // regression guard intact: if the proxy stopped forwarding the outer Host,
                // no recorded call would carry it and this lookup would come back null.
                var operatorFacingHostHeader = $"{operatorFacingHost}:{hostSideMappedPort}";
                var proxyCall = recorded.FirstOrDefault(c =>
                    c.Uri.AbsolutePath.Equals("/api/systems/me/alters", StringComparison.Ordinal)
                    && string.Equals(c.HostHeader, operatorFacingHostHeader, StringComparison.OrdinalIgnoreCase));
                await Assert.That(proxyCall).IsNotNull()
                    .Because($"Expected to record an HttpClient call to /api/systems/me/alters carrying the forwarded outer Host header '{operatorFacingHostHeader}' — its absence means the endpoint proxy either never dialed the relay target or stopped forwarding the upgrade's Host. Recorded calls: [{string.Join(", ", recorded.Select(c => $"({c.Uri}, Host={c.HostHeader ?? "<null>"})"))}].");

                var proxyUri = proxyCall!.Uri;
                await Assert.That(proxyUri.Host).IsNotEqualTo(operatorFacingHost)
                    .Because($"Regression: the proxy is dialing the inbound Host header ({operatorFacingHost}) instead of the local Kestrel listener — this is the exact crash from host/Interfold.Api/Socket/WebSocketHandler.cs:444. Recorded host was '{proxyUri.Host}'.");
                await Assert.That(proxyUri.Port).IsNotEqualTo(hostSideMappedPort)
                    .Because($"Regression: the proxy is dialing the host-side mapped port ({hostSideMappedPort}) instead of the container-internal Kestrel port. Recorded URI was '{proxyUri}'.");

                // The proxy MUST target a loopback host. Under TestServer the helper's
                // null/empty-Addresses fallback produces `http://localhost`; under real
                // Kestrel the helper rewrites wildcard binds to `127.0.0.1`. Accepting
                // either keeps the test stable across both the in-process harness here
                // and any future test variant that runs against a real Kestrel.
                var isLoopback = proxyUri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
                await Assert.That(isLoopback).IsTrue()
                    .Because($"Expected the proxy to dial a loopback host (localhost / 127.0.0.1 / ::1). Recorded URI was '{proxyUri}'.");

                // Regression guard for the loopback-origin leak in WebSocketHandler.
                // HandleEndpointProxyAsync: before the fix, the proxy left
                // `request.Headers.Host` unset on the inner HttpRequestMessage, so under
                // real Kestrel the inner controller saw `Request.Host` = the loopback
                // dial target (e.g. `127.0.0.1:5101`) and stamped unreachable URLs in
                // phx_reply payloads (avatar URLs, OAuth callbacks, etc.). The fix sets
                // the Host header on the inner self-call to the outer upgrade's host.
                //
                // We assert on the OUTBOUND `HttpRequestMessage.Headers.Host` (what the
                // production code intentionally set) rather than the inner
                // `HttpContext.Request.Host` because the latter depends on TestHost's
                // ClientHandler propagating the Host header into the in-process
                // HttpContext — that propagation is not reliable across platforms (.NET
                // 10.0.9 on Linux observed to drop it). Real Kestrel always honors the
                // Host header from the HTTP wire as a matter of HTTP standard, so the
                // outbound assertion is sufficient for production correctness.
                await Assert.That(proxyCall!.HostHeader).IsEqualTo(operatorFacingHostHeader)
                    .Because($"Regression: the proxy must forward the OUTER upgrade's Host header ('{operatorFacingHostHeader}') onto the inner HttpRequestMessage so the inner pipeline observes the operator-facing origin, not the loopback dial target. Recorded Host header was '{proxyCall.HostHeader ?? "<null>"}'.");
            }

            await ws.CloseTestDoneAsync(token);
        }
        finally
        {
            // Clear the recorder hook before any other test sees the factory; the
            // [NotInParallel] key guarantees no overlap but resetting in `finally`
            // keeps the post-condition obvious to readers.
            fixture.Factory.OutboundHttpUriRecorder = null;
        }
    }

    // JoinTopicAsync + WebSocketBasePath moved to Interfold.IntegrationTests.TestServices.WebSocketExtensions
    // during the Phase-6 test-project split so Shared plumbing can join without a circular ref.

    async Task<PhoenixReplyPayload<object>> SendJoinAndReceiveReplyAsync(WebSocket ws, string socketToken, string systemId, string refId, CancellationToken token)
    {
        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = $"system:{systemId}",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = new SocketToken(socketToken), IsReconnect = true },
            Ref = refId,
            JoinRef = "1"
        };

        await ws.SendTextFrameAsync(joinFrame.ToBytes(), token);
        var frame = await ReceivedPhxFrame.ReceiveAsync(ws, token);
        return frame.Reply<object>();
    }

    static int ExtractAlterIdFromEndpointReply(ReceivedPhxFrame replyFrame)
    {
        var reply = replyFrame.Reply<SocketEndpointProxyResponse>();
        if (string.IsNullOrWhiteSpace(reply.Response.Body))
            throw new InvalidOperationException("Endpoint proxy response body is empty — cannot extract alter ID.");

        using var bodyDoc = JsonDocument.Parse(reply.Response.Body);
        var root = bodyDoc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
            return id;

        throw new InvalidOperationException($"Could not parse alter id from endpoint reply body: {reply.Response.Body}");
    }

    static Uri WebSocketBasePath(TestServer server)
    {
        return WebSocketExtensions.WebSocketBasePath(server);
    }
}
