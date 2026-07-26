using Interfold.Api.Services;

namespace Interfold.IntegrationTests.SimplyPluralImport;

/// <summary>
/// Unit coverage for <see cref="SimplyPluralImportService.IsSpCdnUrl"/>. The discriminator
/// gates whether an arbitrary <c>members.avatarUrl</c> / <c>users.avatarUrl</c> string gets
/// queued for SP CDN rehost or stored as an external passthrough URL — the latter is a
/// one-way trip and would silently 404 after Apparyllis sunsets, so a misclassification
/// here means lost avatars. The host list was widened from `spaces.apparyllis.com` alone
/// to also cover the canonical post-v1.12 upload URL (`serve.apparyllis.com`) and the
/// legacy DigitalOcean Spaces hostname; this test pins all three.
/// </summary>
public sealed class SimplyPluralImportServiceIsSpCdnUrlTests : BaseEndpointTest
{
    [Test]
    [Arguments("https://spaces.apparyllis.com/avatars/uid-synth/avatar-uuid-synth")]
    [Arguments("https://serve.apparyllis.com/avatars/uid-synth/avatar-uuid-synth")]
    [Arguments("https://simply-plural.sfo3.digitaloceanspaces.com/avatars/uid-synth/avatar-uuid-synth")]
    [Arguments("HTTPS://SPACES.APPARYLLIS.COM/avatars/uid-synth/avatar-uuid-synth")]
    [Arguments("HTTPS://Serve.Apparyllis.com/avatars/uid-synth/avatar-uuid-synth")]
    public async Task IsSpCdnUrl_ReturnsTrue_ForEverySpHostedAvatarVariant(string url)
    {
        await Assert.That(SimplyPluralImportService.IsSpCdnUrl(url)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("https://example.com/avatars/uid-synth/avatar-uuid-synth")]
    [Arguments("http://spaces.apparyllis.com/avatars/uid-synth/avatar-uuid-synth")] // plain http, not https
    [Arguments("//spaces.apparyllis.com/avatars/uid-synth/avatar-uuid-synth")]      // protocol-relative
    [Arguments("ftp://spaces.apparyllis.com/avatars/uid-synth/avatar-uuid-synth")]
    [Arguments("https://dist.apparyllis.com/resources/Logo_Apparyllis_Square_1024.png")] // SP-owned but
                                                                                          // not an avatar
                                                                                          // CDN — content
                                                                                          // CDN only
    public async Task IsSpCdnUrl_ReturnsFalse_ForEverythingElse(string? url)
    {
        await Assert.That(SimplyPluralImportService.IsSpCdnUrl(url)).IsFalse();
    }

    /// <summary>
    /// Pins the documented limitation of the current <see cref="SimplyPluralImportService.IsSpCdnUrl"/>
    /// matcher: it's a case-insensitive <c>StartsWith</c>, so attacker-controlled URLs whose host
    /// has an SP-host as a literal prefix (e.g. <c>spaces.apparyllis.com.evil.test</c>) currently
    /// pass. That predates this change — the original single-host check had the same shape — but
    /// we lock the behaviour here so any future tightening (e.g. <c>Uri.Host</c> equality) is
    /// observably a behaviour change, not a silent one.
    /// </summary>
    [Test]
    public async Task IsSpCdnUrl_StartsWithSemantics_AcceptsSuffixedHostsAsKnownGap()
    {
        // Self-SSRF only: the SP token is user-supplied, so the worst case is a user mis-importing
        // their own attacker-controlled URL. Tightening this requires switching to Uri.Host
        // equality and is intentionally left out of the host-list widening patch.
        await Assert.That(SimplyPluralImportService.IsSpCdnUrl(
            "https://spaces.apparyllis.com.evil.test/avatars/uid-synth/avatar-uuid-synth")).IsTrue();
    }
}
