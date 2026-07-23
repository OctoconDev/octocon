using Interfold.Api.Socket;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.Socket;

// WebSocketHandler.IsTokenSubjectAuthorizedForTopic — region-prefix-tolerant equality
// gating a phx_join against the token's sub claim. JWTs carry scoped {region}:{rawId}
// subs but socket topics stay raw system:{rawId}; without tolerance the gate would
// 401 valid joins the middleware / SystemTopic.IdMatches accept. Sub-side rejection
// matrix lives in ScopedSystemIdTests; this file pins the comparison contract itself.
public sealed class IsTokenSubjectAuthorizedForTopicTests
{
    private const string RawId = "sys-abcdef0123456789";

    [Test]
    public async Task ScopedSub_RawTopic_SameRawId_IsAuthorized()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        SystemId topic = new(RawId);

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsTrue()
            .Because("JWTs carry a scoped nam:sys-... sub while the socket topic stays raw — this is the exact shape that would 401 the loopback endpoint proxy path without strip-tolerant equality.");
    }

    [Test]
    public async Task ScopedSub_ScopedTopic_SameRawId_IsAuthorized()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        SystemId topic = new($"nam:{RawId}");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsTrue()
            .Because("Scoped-on-both-sides is the wire shape once every client sends fully-qualified topics — the topic-side StripRegionPrefix collapses onto RawId and equality holds.");
    }

    [Test]
    public async Task ScopedSub_DifferentRegionOnTopic_SameRawId_IsAuthorized()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        SystemId topic = new($"eur:{RawId}");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsTrue()
            .Because("Region is stripped from the topic side (sub side already carries an authoritative RawId). Enforcing region equality is not this helper's job — that's what the region-context lookup / persistence layer decides.");
    }

    [Test]
    public async Task ScopedSub_RawTopic_DifferentRawIds_IsRejected()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        SystemId topic = new("sys-someone-else");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsFalse()
            .Because("Sub's RawId and topic's stripped RawId must actually match — the strip on either side is a canonicalisation, not a wildcard.");
    }

    [Test]
    public async Task ScopedSub_ScopedTopic_DifferentRawIds_IsRejected()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        SystemId topic = new("nam:sys-someone-else");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsFalse()
            .Because("Same region prefix on the topic side must not mask a raw-id mismatch.");
    }

    // Defence-in-depth null guards; upstream caller already returns
    // InvalidSocketTokenSubject / UnauthorizedTopic before entering this helper.

    [Test]
    public async Task NullScopedSub_IsRejected()
    {
        SystemId topic = new(RawId);

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: null, requestedSystemId: topic);

        await Assert.That(authorized).IsFalse()
            .Because("Null scoped-sub can never authorise — the upstream gate treats a failed TryParseScoped as InvalidSocketTokenSubject and returns before we're called, and the defence-in-depth null guard here is what backs that upstream contract for any future caller.");
    }

    [Test]
    public async Task NullRequestedSystemId_IsRejected()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(tokenSubject: scopedSub, requestedSystemId: null);

        await Assert.That(authorized).IsFalse()
            .Because("A null requested id (non-system topic, or SystemTopic.TryParse returning false) must not authorise — mirror the sub-side null guard.");
    }
}
