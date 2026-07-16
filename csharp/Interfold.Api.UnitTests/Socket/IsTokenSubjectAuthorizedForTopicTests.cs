using Interfold.Api.Socket;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Socket;

/// <summary>
/// Unit tests for <see cref="WebSocketHandler.IsTokenSubjectAuthorizedForTopic"/> — the
/// region-prefix-tolerant equality that gates a <c>phx_join</c> against the token's
/// <c>sub</c> claim. Every JWT reaching an Interfold controller must carry a scoped
/// <c>{region}:{rawId}</c> sub, but socket topics on the wire stay in raw
/// <c>system:{rawId}</c> form; without this tolerance the gate would 401 scoped-sub
/// JWTs joining raw-topic channels that the middleware, <c>SystemTopic.IdMatches</c>,
/// and <c>InProcessEventBus.PublishAsync</c>'s filter all accept.
///
/// <para>
/// The helper's contract is typed <see cref="ScopedSystemId"/> on the sub side and
/// <see cref="SystemId"/> on the topic side, so raw-string subs are type-unreachable
/// here — <see cref="ScopedSystemId.TryParseScoped"/> rejection matrix coverage lives in
/// <c>ScopedSystemIdTests</c>. What remains here is the actual comparison contract:
/// scoped-sub vs raw/scoped topic tolerance, cross-region tolerance, same-shape identity
/// rejection, and the null guards. Pins the tolerance matrix in the fast unit-test tier
/// so a regression to strict raw-string equality doesn't hide inside a WebSocket timeout.
/// </para>
/// </summary>
public sealed class IsTokenSubjectAuthorizedForTopicTests
{
    // A representative raw system id — mirrors what UniqueId("sys-...") produces at the
    // integration-test layer, but pinned here so a rename of the test generator can't
    // silently change what shape this suite is exercising.
    private const string RawId = "sys-abcdef0123456789";

    // ------------------------------------------------------------------------------------
    // The tolerance matrix: scoped sub × raw/scoped topic. Raw-sub cells are type-
    // unreachable via ScopedSystemId?, so the matrix collapses to these two rows.
    // ------------------------------------------------------------------------------------

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

    // ------------------------------------------------------------------------------------
    // Cross-region: the topic-side strip peels ANY known region prefix, so the same raw id
    // under different regions still authorises. This matches SystemTopic.IdMatches and
    // InProcessEventBus.PublishAsync — no region gate lives here.
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task ScopedSub_DifferentRegionOnTopic_SameRawId_IsAuthorized()
    {
        var scopedSub = ScopedSystemId.ParseScoped($"nam:{RawId}");
        SystemId topic = new($"eur:{RawId}");

        var authorized = WebSocketHandler.IsTokenSubjectAuthorizedForTopic(scopedSub, topic);

        await Assert.That(authorized).IsTrue()
            .Because("Region is stripped from the topic side (sub side already carries an authoritative RawId). Enforcing region equality is not this helper's job — that's what the region-context lookup / persistence layer decides.");
    }

    // ------------------------------------------------------------------------------------
    // The negative cases: different raw ids must fail, regardless of the topic's prefix
    // shape. These pin the "still actually checking identity" contract so the tolerance
    // can't decay into "always true".
    // ------------------------------------------------------------------------------------

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

    // ------------------------------------------------------------------------------------
    // Defence-in-depth: null inputs on either side return false rather than throwing. The
    // upstream caller in IsSocketJoinTokenAuthorizedAsync already returns
    // InvalidSocketTokenSubject on a null-scoped-sub (TryParseScoped failed) and never
    // enters this helper with a null topic (SystemTopic.TryParse-fail branch passes null
    // and the caller returns UnauthorizedTopic without invoking us). But keeping the
    // guards means unit tests / any future non-socket caller doesn't need to hand-craft a
    // "non-null" precondition.
    //
    // Note: there are no sub-side null/empty/whitespace/unknown-prefix rejection tests
    // here — those inputs can't be constructed as a ScopedSystemId in the first place.
    // See ScopedSystemIdTests.TryParseScoped_InvalidInputs_ReturnsFalse for the
    // upstream rejection matrix.
    // ------------------------------------------------------------------------------------

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
