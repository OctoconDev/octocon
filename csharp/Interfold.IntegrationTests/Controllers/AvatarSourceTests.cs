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
public class AvatarSourceTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    private const string ExternalSystemAvatar = "https://cdn.example.com/system-avatar.png";
    private const string ExternalAlterAvatar  = "https://cdn.example.com/alter-avatar.png";

    [Test]
    public async Task Api_SettingsAvatarByUrl_PersistsAndExposesExternalSource()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principalId = TestIds.NewSystemId("sys-extavatar", maxLen: 24);
        await EnsureUserExistsAsync(client, principalId);

        using var uploadResponse = await client.SendAsJsonAsync(
            HttpMethod.Put, "/api/settings/avatar",
            new AvatarUrlUploadRequest(new AvatarUrl(ExternalSystemAvatar)),
            principalId);
        await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var profileResponse = await client.SendAuthedGetAsync($"/api/systems/{principalId}", principalId);
        var profile = await profileResponse.ReadEnvelopeAsync<PublicSystemReadModel>(HttpStatusCode.OK);

        using (Assert.Multiple())
        {
            // Passthrough: stored URL is returned verbatim, never qualified with the API origin.
            await Assert.That(profile.Data.AvatarUrl?.Value).IsEqualTo(ExternalSystemAvatar);
            await Assert.That(profile.Data.AvatarSource).IsEqualTo(AvatarSource.External);
        }
    }

    [Test]
    public async Task Api_AlterAvatarByUrl_PersistsAndExposesExternalSource()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principalId = TestIds.NewSystemId("sys-altext", maxLen: 20);

        var alterId = await CreateAlterAsync(client, principalId, "ExternalAvatarAlter", "external-alter", VisibilityLevel.Public);

        using var uploadResponse = await client.SendAsJsonAsync(
            HttpMethod.Put, $"/api/systems/me/alters/{alterId}/avatar",
            new AvatarUrlUploadRequest(new AvatarUrl(ExternalAlterAvatar)),
            principalId);
        await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var alterResponse = await client.SendAuthedGetAsync($"/api/systems/{principalId}/alters/{alterId}", principalId);
        var alter = await alterResponse.ReadEnvelopeAsync<BareAlter>(HttpStatusCode.OK);

        using (Assert.Multiple())
        {
            await Assert.That(alter.Data.AvatarUrl?.Value).IsEqualTo(ExternalAlterAvatar);
            await Assert.That(alter.Data.AvatarSource).IsEqualTo(AvatarSource.External);
        }
    }

    /// <summary>
    /// Shape-only validator covers ftp/file/relative URLs and over-length values uniformly.
    /// We use one of the disallowed-scheme cases as the canonical 400 contract.
    /// </summary>
    [Test]
    public async Task Api_SettingsAvatarByUrl_RejectsNonHttpUrl()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principalId = TestIds.NewSystemId("sys-bad", maxLen: 16);
        await EnsureUserExistsAsync(client, principalId);

        // AvatarUrl's constructor doesn't validate, so the typed record carries the
        // ftp:// string through to the server unchanged. The 400 + avatar_url_invalid
        // contract is the server-side rule under test.
        using var uploadResponse = await client.SendAsJsonAsync(
            HttpMethod.Put, "/api/settings/avatar",
            new AvatarUrlUploadRequest(new AvatarUrl("ftp://example.com/avatar.png")),
            principalId);

        var error = await uploadResponse.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.AvatarUrlInvalid);
    }

    [Test]
    public async Task Api_AlterAvatarByUrl_RejectsRelativeUrl()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principalId = TestIds.NewSystemId("sys-altrel", maxLen: 20);

        var alterId = await CreateAlterAsync(client, principalId, "RelativeAvatarAlter", "relative-alter");

        // Same rationale as the sibling test — AvatarUrl accepts anything, and the
        // server enforces the http(s)-scheme rule at validation time.
        using var uploadResponse = await client.SendAsJsonAsync(
            HttpMethod.Put, $"/api/systems/me/alters/{alterId}/avatar",
            new AvatarUrlUploadRequest(new AvatarUrl("/relative/path.png")),
            principalId);

        var error = await uploadResponse.ReadErrorAsync(HttpStatusCode.BadRequest);
        await Assert.That(error.Code).IsEqualTo(ErrorCodes.AvatarUrlInvalid);
    }

    // Isolation contract: this test builds its OWN InterfoldWebApplicationFactory via
    // IWebFactoryFixture.CreatePrivateFactory() rather than mutating fixture.Factory
    // (the session-shared one). The previous version wrote OCTOCON_AVATAR_PUBLIC_BASE
    // and OCTOCON_AVATAR_STORAGE_ROOT via WithConfiguration on the shared factory —
    // because StorageConfiguration binds via IOptionsMonitor, those writes cascaded
    // live into every subsequent test's request pipeline, and any test that resolved
    // IOptionsMonitor<StorageConfiguration>.Get() (e.g. via InterfoldPrincipalMiddleware)
    // afterwards would throw OptionsValidationException on the AvatarPublicBase's
    // [AbsoluteHttpUri] rule. That poisoning cascade produced ~50 downstream 500s in
    // the full suite. Owning our own factory closes that vector — nothing this test
    // writes can ever be seen by a test that runs on fixture.Factory.
    //
    // NotInParallel is retained on the shared "avatar-storage-config" bucket so the
    // two SettingsControllerTests siblings (which also build private factories) don't
    // run three factory builds concurrently for the same backend — pending the
    // Group-B Npgsql pool sizing fix, parallel builds can exhaust the default pool of
    // 5 connections during SecretsPreBuildLoader's Postgres fetch. Once Group B lands
    // this attribute can be removed.
    [Test, NotInParallel("avatar-storage-config")]
    public async Task Api_SettingsAvatarMultipart_ReportsLocalSource()
    {
        await IsolatedAvatarStorage.RunAsync(fixture, async (client, publicBasePath) =>
        {
            var principalId = TestIds.NewSystemId("sys-localavatar", maxLen: 24);

            using var uploadRequest = BuildMultipartUploadRequest(client, "/api/settings/avatar", principalId, "avatar.png", "image/png");
            using var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var profileResponse = await client.SendAuthedGetAsync($"/api/systems/{principalId}", principalId);
            var profile = await profileResponse.ReadEnvelopeAsync<PublicSystemReadModel>(HttpStatusCode.OK);

            using (Assert.Multiple())
            {
                await Assert.That(profile.Data.AvatarSource).IsEqualTo(AvatarSource.Local);
                // Local avatars get the request origin prepended; the URL path must sit
                // under our configured public base so external observers can fetch the bytes.
                await Assert.That(UrlPathStartsWith(profile.Data.AvatarUrl?.Value, $"{publicBasePath}/{principalId}/self/")).IsTrue();
            }

            // End-to-end serving check (mirrors the sibling assertion in
            // SettingsControllerTests.Api_SettingsAvatarMultipart_PersistsAndServesAvatar):
            // the avatar URL the SPA receives MUST be servable. This test covers the
            // local-avatar-source codepath specifically — the assertion sits here, not in
            // the source-only happy-path tests above, because this is the only test in
            // the file that exercises a real multipart upload (the others use external
            // URLs and never persist bytes).
            using var avatarRequest = new HttpRequestMessage(HttpMethod.Get, profile.Data.AvatarUrl?.Value);
            // Anonymous on purpose — see the sibling test for the rationale (URL is the capability).
            using var avatarResponse = await client.SendAsync(avatarRequest);
            var avatarBytes = await avatarResponse.Content.ReadAsByteArrayAsync();
            using (Assert.Multiple())
            {
                await Assert.That(avatarResponse.StatusCode).IsEqualTo(HttpStatusCode.OK)
                    .Because($"Expected the local-source avatar URL to be servable. URL was '{profile.Data.AvatarUrl}'.");
                await Assert.That(avatarBytes.Length).IsGreaterThan(0)
                    .Because("Expected non-zero bytes back from the avatar GET.");
            }
        });
    }
}

