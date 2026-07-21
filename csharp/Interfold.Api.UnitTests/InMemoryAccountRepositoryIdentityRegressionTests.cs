using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Api.UnitTests.Support;

namespace Interfold.Api.UnitTests;

/// <summary>
/// Regression suite for the OAuth-identity paths on <see cref="InMemoryAccountRepository"/>
/// — <see cref="InMemoryAccountRepository.FindOrCreateSystemIdAsync"/>,
/// <see cref="InMemoryAccountRepository.UnlinkDiscordAsync"/>/<c>Email</c>/<c>Apple</c>, and
/// the identity cleanup inside <see cref="InMemoryAccountRepository.DeleteAsync"/>.
///
/// <para>
/// The three per-provider FindOrCreate bodies (discord / email / apple) and the three
/// per-provider Unlink bodies were previously hand-copied. Round-2 C2 collapsed them onto
/// two generic helpers keyed by the identity wrapper type + a static extractor lambda.
/// The failure mode a copy-paste-style refactor of that kind introduces is a wrong dict
/// pair fed to the generic — e.g. an Unlink-discord that scrubs the email reverse map.
/// The isolation tests below pin that dict-pair-per-branch invariant so a swap goes
/// noisy immediately.
/// </para>
///
/// <para>
/// The Scylla adapter's parallel identity paths are already factored via
/// <c>UnlinkIdentityAsync(systemId, ProviderColumn)</c>; those are covered by
/// integration-tier fixtures. Direct unit coverage is only meaningful for the InMemory
/// repo because it holds all state in-process and every branch is observable through the
/// public <see cref="IAccountRepository"/> surface without a live backend.
/// </para>
/// </summary>
public sealed class InMemoryAccountRepositoryIdentityRegressionTests
{
    private const string DiscordValue = "discord-user-123";
    private const string EmailValue = "user@example.com";
    private const string EmailValueUpper = "USER@EXAMPLE.COM";
    private const string AppleValue = "001234.deadbeef.5678";



    private static InMemoryAccountRepository NewRepo() =>
        new(new FixedRegionContext(ScyllaKeyspace.Nam));

    /// <summary>
    /// Provision a user via <see cref="ProviderIdentity.FromDiscord"/> and then attach
    /// email + apple identities. <see cref="InMemoryAccountRepository.LinkIdentityToUserAsync"/>
    /// requires the user to have at least one of username / description / avatar / linkToken
    /// present before it will accept a link — an OAuth-only auto-provisioned user without
    /// any profile field is treated as <c>UserNotFound</c> by the link path. Seeding a
    /// username first mirrors the real-life onboarding order (first sign-in sets a
    /// username, then the user optionally connects additional identities under Settings).
    /// </summary>
    private static async Task<SystemId> ProvisionAllThreeIdentitiesAsync(InMemoryAccountRepository repo)
    {
        var systemId = await repo.FindOrCreateSystemIdAsync(ProviderIdentity.FromDiscord(new(DiscordValue)));
        await Assert.That(systemId).IsNotNull()
            .Because("Setup precondition: initial FindOrCreate via Discord must mint a SystemId.");

        await repo.UpdateUsernameAsync(systemId!.Value, new("test-user"));
        await repo.LinkIdentityToUserAsync(systemId.Value, ProviderIdentity.FromGoogle(new(EmailValue)));
        await repo.LinkIdentityToUserAsync(systemId.Value, ProviderIdentity.FromApple(new(AppleValue)));

        return systemId.Value;
    }

    // ---------------- FindOrCreate: auto-provision + idempotency ------------

    [Test]
    public async Task FindOrCreateSystemIdAsync_DiscordMiss_AutoProvisionsAndSecondCallReturnsSameId()
    {
        var repo = NewRepo();
        var identity = ProviderIdentity.FromDiscord(new(DiscordValue));

        var first = await repo.FindOrCreateSystemIdAsync(identity);
        var second = await repo.FindOrCreateSystemIdAsync(identity);

        await Assert.That(first).IsNotNull()
            .Because("A miss on a valid Discord id must auto-provision — this is the OAuth-login shape's core contract.");
        await Assert.That(second).IsEqualTo(first)
            .Because("A second FindOrCreate for the same Discord id must return the SAME SystemId — otherwise every login would mint a fresh phantom account and orphan the previous one.");
    }

    [Test]
    public async Task FindOrCreateSystemIdAsync_EmailMiss_AutoProvisionsAndSecondCallReturnsSameId()
    {
        var repo = NewRepo();
        var identity = ProviderIdentity.FromGoogle(new(EmailValue));

        var first = await repo.FindOrCreateSystemIdAsync(identity);
        var second = await repo.FindOrCreateSystemIdAsync(identity);

        await Assert.That(first).IsNotNull()
            .Because("A miss on a valid Google email must auto-provision — Google-OAuth login relies on this branch.");
        await Assert.That(second).IsEqualTo(first)
            .Because("A second FindOrCreate for the same email must return the SAME SystemId — otherwise Google-OAuth users would double-provision on every login.");
    }

    [Test]
    public async Task FindOrCreateSystemIdAsync_AppleMiss_AutoProvisionsAndSecondCallReturnsSameId()
    {
        var repo = NewRepo();
        var identity = ProviderIdentity.FromApple(new(AppleValue));

        var first = await repo.FindOrCreateSystemIdAsync(identity);
        var second = await repo.FindOrCreateSystemIdAsync(identity);

        await Assert.That(first).IsNotNull()
            .Because("A miss on a valid Apple id must auto-provision — Sign in with Apple relies on this branch.");
        await Assert.That(second).IsEqualTo(first)
            .Because("A second FindOrCreate for the same Apple id must return the SAME SystemId — otherwise Apple-OAuth users would double-provision on every login.");
    }

    // ---------------- FindOrCreate: case-insensitivity on Email --------------

    [Test]
    public async Task FindOrCreateSystemIdAsync_EmailCasedDifferently_ReturnsSameSystemId()
    {
        var repo = NewRepo();
        var lower = await repo.FindOrCreateSystemIdAsync(ProviderIdentity.FromGoogle(new(EmailValue)));
        var upper = await repo.FindOrCreateSystemIdAsync(ProviderIdentity.FromGoogle(new(EmailValueUpper)));

        await Assert.That(upper).IsEqualTo(lower)
            .Because("The email reverse-map keys on StringComparer.OrdinalIgnoreCase — a caller submitting the same address in a different case must land on the same account. Generic-helper refactor risk: if the generic ever swapped the reverse-map for a case-sensitive dict, the two calls above would provision two separate accounts.");
    }

    // ---------------- Unlink: round-trip + no-op idempotency ----------------

    [Test]
    public async Task UnlinkDiscordAsync_AfterAutoProvision_SubsequentTryFindReturnsNull()
    {
        var repo = NewRepo();
        var identity = ProviderIdentity.FromDiscord(new(DiscordValue));
        var systemId = await repo.FindOrCreateSystemIdAsync(identity);
        await Assert.That(systemId).IsNotNull()
            .Because("Setup: FindOrCreate must succeed so the subsequent unlink has something to remove.");

        var unlinked = await repo.UnlinkDiscordAsync(systemId!.Value);
        await Assert.That(unlinked).IsTrue()
            .Because("UnlinkDiscordAsync returns true on success — the return-shape contract holds regardless of whether the identifier was set.");

        var afterUnlink = await repo.TryFindSystemIdByDiscordIdAsync(new(DiscordValue));
        await Assert.That(afterUnlink).IsNull()
            .Because("After Unlink, the reverse (raw discord id → SystemId) lookup must return null — otherwise the account is discoverable by a discord id it no longer claims.");
    }

    [Test]
    public async Task UnlinkDiscordAsync_UserWithNothingLinked_ReturnsTrue()
    {
        var repo = NewRepo();
        // A user that has never touched the identity dicts — we still expect true so the
        // command layer treats "nothing to unlink" as an idempotent success rather than a
        // 404-shaped surface error. This matches the Scylla adapter's contract.
        var result = await repo.UnlinkDiscordAsync(new("nam:never-linked"));

        await Assert.That(result).IsTrue()
            .Because("Unlink on a user with no linked Discord id must return true — parity with the Scylla adapter, which returns true for the same shape and drives the settings-command flow's Accepted/Replay envelope.");
    }

    // ---------------- Unlink: dict-pair isolation (the refactor's core risk) -

    [Test]
    public async Task UnlinkDiscordAsync_DoesNotAffectEmailOrAppleReverseMaps()
    {
        var repo = NewRepo();
        // All three identities linked onto the SAME user — see ProvisionAllThreeIdentitiesAsync
        // for why the intermediate UpdateUsernameAsync is required.
        var systemId = await ProvisionAllThreeIdentitiesAsync(repo);

        var linked = await repo.UnlinkDiscordAsync(systemId);
        await Assert.That(linked).IsTrue()
            .Because("Setup: the unlink itself must succeed so the isolation assertion below is meaningful.");

        var profile = await repo.GetPublicProfileAsync(systemId);
        await Assert.That(profile).IsNotNull()
            .Because("The user still has a username, email, and apple identity — their public profile must survive an UnlinkDiscord that only targets one of three dict pairs.");
        await Assert.That(profile!.DiscordId).IsNull()
            .Because("UnlinkDiscord must scrub the discord reverse map.");
        await Assert.That(profile.Email).IsNotNull()
            .Because("UnlinkDiscord must NOT touch the email dict pair — this is the refactor risk (generic helper receiving the wrong dict pair) the test is pinned to catch.");
        await Assert.That(profile.AppleId).IsNotNull()
            .Because("UnlinkDiscord must NOT touch the apple dict pair — see the email pin above for the refactor-risk rationale.");
    }

    // ---------------- Delete: unlinks all three identity dict pairs ----------

    [Test]
    public async Task DeleteAsync_UserWithAllThreeIdentities_ScrubsAllThreeReverseMaps()
    {
        var repo = NewRepo();
        var systemId = await ProvisionAllThreeIdentitiesAsync(repo);

        var deleted = await repo.DeleteAsync(systemId);
        await Assert.That(deleted).IsTrue()
            .Because("DeleteAsync returns true on success — matches the Scylla adapter's contract.");

        // Discord has a direct TryFind entry point; the other two only surface through
        // GetPublicProfileAsync or a fresh FindOrCreate. The assertion is the same:
        // post-delete the account must be undiscoverable from every side, otherwise a
        // follow-up OAuth login by the same identity would silently re-adopt the tombstone.
        await Assert.That(await repo.TryFindSystemIdByDiscordIdAsync(new(DiscordValue))).IsNull()
            .Because("Delete must scrub the discord reverse-map — a fresh FindOrCreate by the same discord id must not resurrect the deleted account.");

        var freshFromEmail = await repo.FindOrCreateSystemIdAsync(ProviderIdentity.FromGoogle(new(EmailValue)));
        await Assert.That(freshFromEmail).IsNotEqualTo(systemId)
            .Because("Delete must scrub the email reverse-map. Otherwise a follow-up OAuth login with the same email would silently re-adopt the just-deleted SystemId instead of provisioning a fresh account.");

        var freshFromApple = await repo.FindOrCreateSystemIdAsync(ProviderIdentity.FromApple(new(AppleValue)));
        await Assert.That(freshFromApple).IsNotEqualTo(systemId)
            .Because("Delete must scrub the apple reverse-map. Symmetric to the email pin above — a follow-up Sign in with Apple would otherwise adopt the deleted SystemId.");
    }
}
