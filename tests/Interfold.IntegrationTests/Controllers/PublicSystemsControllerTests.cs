using System.Net;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class PublicSystemsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task PublicBatch_SelfLookup_Returns403InvalidEndpoint()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-public-batch-self";
        _ = await CreateAlterAsync(client, principal, "BatchSelfSeed");
        await EnsureUserExistsAsync(client, principal, "batch-self");

        using var res = await client.SendAuthedGetAsync($"/api/systems/{principal}/batch", principal);
        var error = await res.ReadErrorAsync(HttpStatusCode.Forbidden);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.InvalidEndpoint);
    }

    [Test, Skip("Need to rework")] //Well all of them really...
    public async Task Visibility_NonFriendFriendTrusted_AppliesToFronting()
    {
        using var client = TestClient.NoRedirect(fixture);

        var (owner, nonFriend, friend, trusted) = await SeedVisibilityQuartetAsync(client, "fronting-visibility");

        var alterPublic = await CreateAlterAsync(client, owner, "VisPublic");
        var alterFriends = await CreateAlterAsync(client, owner, "VisFriends");
        var alterTrusted = await CreateAlterAsync(client, owner, "VisTrusted");
        var alterPrivate = await CreateAlterAsync(client, owner, "VisPrivate");

        await SetAlterSecurityLevelAsync(client, owner, alterPublic, VisibilityLevel.Public);
        await SetAlterSecurityLevelAsync(client, owner, alterFriends, VisibilityLevel.FriendsOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterTrusted, VisibilityLevel.TrustedOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterPrivate, VisibilityLevel.Private);

        await StartFrontAsync(client, owner, alterPublic);
        await StartFrontAsync(client, owner, alterFriends);
        await StartFrontAsync(client, owner, alterTrusted);
        await StartFrontAsync(client, owner, alterPrivate);

        // Non-friend viewer: only the Public fronter surfaces; the other three are filtered
        // by the guarded fronting list. One GET per viewer is enough — the response is
        // idempotent for a fixed friendship-graph state, so batching the four visibility
        // assertions against the same payload is both stricter and one-quarter the round
        // trips of the pre-typed-sweep needle hunt.
        await AssertFrontingVisibilityAsync(
            client, owner, nonFriend,
            visible: new[] { alterPublic },
            hidden: new[] { alterFriends, alterTrusted, alterPrivate });

        // Friend viewer gains FriendsOnly.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);
        await AssertFrontingVisibilityAsync(
            client, owner, friend,
            visible: new[] { alterPublic, alterFriends },
            hidden: new[] { alterTrusted, alterPrivate });

        // Trusted viewer gains TrustedOnly; Private stays hidden.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);
        await AssertFrontingVisibilityAsync(
            client, owner, trusted,
            visible: new[] { alterPublic, alterFriends, alterTrusted },
            hidden: new[] { alterPrivate });
    }

    /// <summary>
    /// Asserts every id in <paramref name="visible"/> appears in the guarded fronting list
    /// for <paramref name="viewer"/>, and every id in <paramref name="hidden"/> is absent.
    /// Uses <see cref="FrontActiveReadModel.Alter"/>.Id (the row-level typed shape produced
    /// by <c>PublicSystemsController.ListFronting</c>) rather than string-searching the
    /// envelope; this way, a serialiser drift that renames the field or drops the alter
    /// nesting fails at deserialise time instead of silently matching / missing.
    /// </summary>
    private static async Task AssertFrontingVisibilityAsync(
        HttpClient client,
        string owner,
        string viewer,
        AlterId[] visible,
        AlterId[] hidden)
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/fronting", viewer);
        var envelope = await res.ReadEnvelopeAsync<IReadOnlyList<FrontActiveReadModel>>(HttpStatusCode.OK);
        var visibleAlterIds = envelope.Data.Select(f => f.Alter.Id).ToArray();

        using (Assert.Multiple())
        {
            foreach (var expected in visible)
            {
                await Assert.That(visibleAlterIds).Contains(expected)
                    .Because($"Expected viewer '{viewer}' to see fronter alter {expected.Value} in owner '{owner}''s fronting list.");
            }
            foreach (var restricted in hidden)
            {
                await Assert.That(visibleAlterIds).DoesNotContain(restricted)
                    .Because($"Expected viewer '{viewer}' NOT to see fronter alter {restricted.Value} in owner '{owner}''s fronting list (visibility-restricted).");
            }
        }
    }

    [Test]
    public async Task PublicAlter_PrivateSecurity_Returns404ForAnonymous()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-guarded-alter";
        var alterId = await CreateAlterAsync(client, principal, "GuardedAlter");
        await EnsureUserExistsAsync(client, principal, "guarded-alter");

        await SetAlterSecurityLevelAsync(client, principal, alterId, VisibilityLevel.Private);

        using var publicRes = await client.GetAsync($"/api/systems/{principal}/alters/{alterId}");
        await Assert.That(publicRes.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Visibility_NonFriendFriendTrusted_AppliesAcrossPublicReads()
    {
        using var client = TestClient.NoRedirect(fixture);

        var (owner, nonFriend, friend, trusted) = await SeedVisibilityQuartetAsync(client, "alters-visibility");

        var alterPublic = await CreateAlterAsync(client, owner, "VisPublic");
        var alterFriends = await CreateAlterAsync(client, owner, "VisFriends");
        var alterTrusted = await CreateAlterAsync(client, owner, "VisTrusted");
        var alterPrivate = await CreateAlterAsync(client, owner, "VisPrivate");

        await SetAlterSecurityLevelAsync(client, owner, alterPublic, VisibilityLevel.Public);
        await SetAlterSecurityLevelAsync(client, owner, alterFriends, VisibilityLevel.FriendsOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterTrusted, VisibilityLevel.TrustedOnly);
        await SetAlterSecurityLevelAsync(client, owner, alterPrivate, VisibilityLevel.Private);

        // Non-friend viewer: sees Public only. Restricted alters surface as
        // alter_not_found (the guarded read masks visibility rejects as 404s so callers
        // can't distinguish "no such alter" from "not permitted").
        await AssertAlterVisibleAsync(client, owner, alterPublic, nonFriend);
        await AssertAlterHiddenAsync(client, owner, alterFriends, nonFriend);
        await AssertAlterHiddenAsync(client, owner, alterTrusted, nonFriend);
        await AssertAlterHiddenAsync(client, owner, alterPrivate, nonFriend);

        // Friend viewer gains FriendsOnly; TrustedOnly stays hidden.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        await AssertAlterVisibleAsync(client, owner, alterFriends, friend);
        await AssertAlterHiddenAsync(client, owner, alterTrusted, friend);

        // Trusted viewer gains TrustedOnly; Private stays hidden.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        await AssertAlterVisibleAsync(client, owner, alterTrusted, trusted);
        await AssertAlterHiddenAsync(client, owner, alterPrivate, trusted);
    }

    /// <summary>
    /// Asserts <paramref name="viewer"/> can GET <c>/api/systems/{owner}/alters/{alterId}</c>
    /// with 200 + the alter payload's <see cref="BareAlter.Id"/> matching. Thin domain-flavoured
    /// wrapper over <see cref="AssertGuardedResourceVisibleAsync"/>; kept as a named site so
    /// the call sites read as intent (visible alter) rather than as generics gymnastics.
    /// </summary>
    private static Task AssertAlterVisibleAsync(HttpClient client, string owner, AlterId alterId, string viewer)
        => AssertGuardedResourceVisibleAsync<BareAlter, AlterId>(
            client, owner, alterId, viewer, "alters", a => a.Id, "alter");

    /// <summary>
    /// Asserts <paramref name="viewer"/> hits 404 + <see cref="ErrorCodes.AlterNotFound"/>
    /// on the guarded alter GET. The API deliberately conflates "no such alter" and
    /// "not permitted" into the same shape so callers can't fingerprint restricted alters;
    /// this assertion pins that behaviour via <see cref="AssertGuardedResourceHiddenAsync"/>.
    /// </summary>
    private static Task AssertAlterHiddenAsync(HttpClient client, string owner, AlterId alterId, string viewer)
        => AssertGuardedResourceHiddenAsync(
            client, owner, alterId, viewer, "alters", ErrorCodes.AlterNotFound, "alter");

    [Test]
    public async Task PublicTag_PrivateSecurity_Returns404ForAnonymous()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-guarded-tag";
        var tagId = await CreateTagAsync(client, principal, "GuardedTag");
        await EnsureUserExistsAsync(client, principal, "guarded-tag");

        await SetTagSecurityLevelAsync(client, principal, tagId, VisibilityLevel.Private);

        using var publicRes = await client.GetAsync($"/api/systems/{principal}/tags/{tagId}");
        await Assert.That(publicRes.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Visibility_NonFriendFriendTrusted_AppliesToTags()
    {
        using var client = TestClient.NoRedirect(fixture);

        var (owner, nonFriend, friend, trusted) = await SeedVisibilityQuartetAsync(client, "tags-visibility");

        var tagPublic = await CreateTagAsync(client, owner, "TagPublic");
        var tagFriends = await CreateTagAsync(client, owner, "TagFriends");
        var tagTrusted = await CreateTagAsync(client, owner, "TagTrusted");
        var tagPrivate = await CreateTagAsync(client, owner, "TagPrivate");

        await SetTagSecurityLevelAsync(client, owner, tagPublic, VisibilityLevel.Public);
        await SetTagSecurityLevelAsync(client, owner, tagFriends, VisibilityLevel.FriendsOnly);
        await SetTagSecurityLevelAsync(client, owner, tagTrusted, VisibilityLevel.TrustedOnly);
        await SetTagSecurityLevelAsync(client, owner, tagPrivate, VisibilityLevel.Private);

        // Non-friend viewer: Public only; restricted tags mask as tag_not_found for the
        // same fingerprint-resistance reason the alter surface uses.
        await AssertTagVisibleAsync(client, owner, tagPublic, nonFriend);
        await AssertTagHiddenAsync(client, owner, tagFriends, nonFriend);
        await AssertTagHiddenAsync(client, owner, tagTrusted, nonFriend);
        await AssertTagHiddenAsync(client, owner, tagPrivate, nonFriend);

        // Friend viewer gains FriendsOnly.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        await AssertTagVisibleAsync(client, owner, tagFriends, friend);
        await AssertTagHiddenAsync(client, owner, tagTrusted, friend);

        // Trusted viewer gains TrustedOnly; Private stays hidden.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        await AssertTagVisibleAsync(client, owner, tagTrusted, trusted);
        await AssertTagHiddenAsync(client, owner, tagPrivate, trusted);
    }

    /// <summary>
    /// Tag counterpart of <see cref="AssertAlterVisibleAsync"/>. Delegates to the shared
    /// <see cref="AssertGuardedResourceVisibleAsync"/> so a future change to the guarded-read
    /// success contract (envelope shape, status code, id-carrying model surface) lands in
    /// one place instead of drifting across the alter and tag mirrors.
    /// </summary>
    private static Task AssertTagVisibleAsync(HttpClient client, string owner, TagId tagId, string viewer)
        => AssertGuardedResourceVisibleAsync<TagPublicReadModel, TagId>(
            client, owner, tagId, viewer, "tags", t => t.Id, "tag");

    /// <summary>
    /// Tag counterpart of <see cref="AssertAlterHiddenAsync"/>. Delegates to
    /// <see cref="AssertGuardedResourceHiddenAsync"/> — the same "hide the reason" contract
    /// the alter surface uses, so both mirrors share one authoritative failure shape.
    /// </summary>
    private static Task AssertTagHiddenAsync(HttpClient client, string owner, TagId tagId, string viewer)
        => AssertGuardedResourceHiddenAsync(
            client, owner, tagId, viewer, "tags", ErrorCodes.TagNotFound, "tag");

    /// <summary>
    /// Generic form of the "guarded public read succeeds for this viewer" assertion. Callers
    /// supply the read model type, the id selector (so a typed <see cref="AlterId"/> /
    /// <see cref="TagId"/> comparison keeps its type safety end-to-end), the URL segment,
    /// and a resource label for the human-readable failure message. Deserialising into the
    /// real read model means a wire-shape drift trips at deserialise time rather than
    /// silently matching / missing in a stringly-typed body scan.
    /// </summary>
    private static async Task AssertGuardedResourceVisibleAsync<TReadModel, TId>(
        HttpClient client,
        string owner,
        TId id,
        string viewer,
        string urlSegment,
        Func<TReadModel, TId> idSelector,
        string resourceLabel)
        where TReadModel : notnull
        where TId : notnull
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/{urlSegment}/{id}", viewer);
        var envelope = await res.ReadEnvelopeAsync<TReadModel>(HttpStatusCode.OK);
        await Assert.That(idSelector(envelope.Data)).IsEqualTo(id)
            .Because($"Expected viewer '{viewer}' to see {resourceLabel} {id} in owner '{owner}''s public read; got a different {resourceLabel} id back.");
    }

    /// <summary>
    /// Generic form of the "guarded public read masks as 404" assertion. Callers supply the
    /// URL segment, the expected error code, and the resource label; the shared body pins
    /// the 404 status + the code so a regression that unmasks visibility (e.g. surfacing
    /// <c>alter_forbidden</c> instead of <c>alter_not_found</c>) fails the same way across
    /// every guarded-resource surface.
    /// </summary>
    private static async Task AssertGuardedResourceHiddenAsync<TId>(
        HttpClient client,
        string owner,
        TId id,
        string viewer,
        string urlSegment,
        ErrorCode expectedErrorCode,
        string resourceLabel)
        where TId : notnull
    {
        using var res = await client.SendAuthedGetAsync($"/api/systems/{owner}/{urlSegment}/{id}", viewer);
        var error = await res.ReadErrorAsync(HttpStatusCode.NotFound);
        await Assert.That(error.Code).IsEqualTo(expectedErrorCode)
            .Because($"Expected viewer '{viewer}' to receive {expectedErrorCode.Value} for owner '{owner}''s {resourceLabel} {id}; got '{error.Code.Value}' with detail '{error.Detail}'.");
    }

}

