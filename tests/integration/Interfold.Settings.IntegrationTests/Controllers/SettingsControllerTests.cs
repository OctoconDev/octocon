using System.Net;
using Interfold.Api.Models;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class SettingsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task SettingsField_InvalidType_ReturnsBadRequest()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-field-fallback";
        await EnsureUserExistsAsync(client, principal);

        // type = "garbage" — the Elixir server used to fold this to "text", but we deliberately
        // hold every enum boundary to the same fail-fast contract EnumWireExtensions documents.
        // Kept as raw JSON: the whole point is to bypass typed serialisation and prove the
        // server rejects the malformed enum literal at the wire, which a typed FieldType
        // value could never produce.
        using var res = await client.SendRawJsonAsync(
            HttpMethod.Post, "/api/settings/fields",
            "{\"name\":\"FallbackField\",\"type\":\"garbage\"}",
            principal);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SettingsField_MissingType_FallsBackToText_ReturnsCreatedWithId()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-field-missing-type";
        await EnsureUserExistsAsync(client, principal);

        // type absent entirely — the typed record has Type as `FieldType?` and passing null
        // both preserves the "absent" wire shape AND documents the intent at the C# call site.
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/fields",
            new SettingsCreateFieldRequest("NoTypeField", Type: null, SecurityLevel: null, Locked: null),
            principal);

        var envelope = await res.ReadEnvelopeAsync<FieldCreatedResponse>(HttpStatusCode.Created);
        await Assert.That(envelope.Data.Id).IsNotEqualTo(default(FieldId));
    }

    [Test]
    public async Task Idempotency_SettingsUsernameUpdate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            return await client.SendAsJsonAsync(
                HttpMethod.Post, "/api/settings/username",
                new SettingsUsernameRequest(new Username("soakuser")),
                "soak-default-principal",
                idempotencyKey: key);
        });
    }

    [Test]
    public async Task SettingsField_Create_ReturnsCreatedFieldId()
    {
        using var client = TestClient.NoRedirect(fixture);

        var principal = "parity-field-defaults";
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/fields",
            new SettingsCreateFieldRequest("DefaultSecurityField", FieldType.Text, SecurityLevel: null, Locked: null),
            principal);

        var envelope = await res.ReadEnvelopeAsync<FieldCreatedResponse>(HttpStatusCode.Created);
        await Assert.That(envelope.Data.Id).IsNotEqualTo(default(FieldId));
    }

    // Isolation contract for both avatar multipart tests below: each builds its OWN
    // InterfoldWebApplicationFactory via IWebFactoryFixture.CreatePrivateFactory()
    // instead of mutating fixture.Factory. The previous version wrote
    // OCTOCON_AVATAR_STORAGE_ROOT and OCTOCON_AVATAR_PUBLIC_BASE via WithConfiguration
    // on the session-shared factory. StorageConfiguration binds via IOptionsMonitor,
    // so those writes cascaded live into every subsequent test's request pipeline;
    // any test that resolved IOptionsMonitor<StorageConfiguration>.Get() afterwards
    // (e.g. through InterfoldPrincipalMiddleware) would throw
    // OptionsValidationException on the AvatarPublicBase's [AbsoluteHttpUri] rule,
    // producing ~50 downstream 500s in the full suite. Private factories close that
    // vector at the design level — nothing these tests write can ever be seen by a
    // test that runs on fixture.Factory.
    //
    // NotInParallel is retained on the shared "avatar-storage-config" bucket so the
    // three avatar-multipart tests (this one, the alter sibling below, and
    // AvatarSourceTests.Api_SettingsAvatarMultipart_ReportsLocalSource) don't run
    // three factory builds concurrently for the same backend — pending the Group-B
    // Npgsql pool sizing fix, parallel builds can exhaust the default pool of 5
    // connections during SecretsPreBuildLoader's Postgres fetch. Once Group B lands
    // this attribute can be removed.
    [Test, NotInParallel("avatar-storage-config")]
    public async Task Api_SettingsAvatarMultipart_PersistsAndServesAvatar()
    {
        await IsolatedAvatarStorage.RunAsync(fixture, async (client, publicBasePath) =>
        {
            var principalId = TestIds.NewSystemId("sys-avatar", maxLen: 18);

            using var uploadRequest = BuildMultipartUploadRequest(client, "/api/settings/avatar", principalId, "avatar-system.png", "image/png");
            using var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var profileResponse = await client.SendAuthedGetAsync($"/api/systems/{principalId}", principalId);
            var profile = await profileResponse.ReadEnvelopeAsync<PublicSystemReadModel>(HttpStatusCode.OK);

            using (Assert.Multiple())
            {
                await Assert.That(profile.Data.AvatarUrl).IsNotNull();
                await Assert.That(UrlPathStartsWith(profile.Data.AvatarUrl?.Value, $"{publicBasePath}/{principalId}/self/")).IsTrue();
            }

            // End-to-end serving check: the avatar_url that the SPA receives from the
            // profile response must actually return the bytes when fetched. Before the
            // Phase-2 middleware in Program.cs this would 404 — the API wrote the file
            // to disk and stamped a URL, but no handler served the bytes back. The
            // assertion below is the regression guard for that "URL stamped but no
            // listener" gap. We do it inside the same test so any future shape change
            // to LocalAvatarStorage's URL format and the middleware's path matching is
            // caught in one place.
            using var avatarRequest = new HttpRequestMessage(HttpMethod.Get, profile.Data.AvatarUrl?.Value);
            // Avatar GETs are unauthenticated (the URL itself is the capability — this
            // is the same exposure model as a CDN-fronted setup). Anonymous request is
            // intentional so the assertion reflects what the SPA / external embed sees.
            using var avatarResponse = await client.SendAsync(avatarRequest);
            var avatarBytes = await avatarResponse.Content.ReadAsByteArrayAsync();
            using (Assert.Multiple())
            {
                await Assert.That(avatarResponse.StatusCode).IsEqualTo(HttpStatusCode.OK)
                    .Because($"Expected the avatar URL returned by the profile endpoint to be servable. URL was '{profile.Data.AvatarUrl}'.");
                await Assert.That(avatarBytes.Length).IsGreaterThan(0)
                    .Because("Expected non-zero bytes back from the avatar GET — a zero-length response would indicate the middleware matched the path but couldn't read the file off disk.");
            }
        });
    }

    // Isolation contract: see the sibling Api_SettingsAvatarMultipart_PersistsAndServesAvatar
    // above for the full rationale — this test also builds its OWN factory via
    // CreatePrivateFactory so its avatar-storage config mutations never leak into
    // fixture.Factory and cannot poison other tests in the session.
    [Test, NotInParallel("avatar-storage-config")]
    public async Task Api_AlterAvatarMultipart_PersistsAndReflectsOnPublicAlter()
    {
        await IsolatedAvatarStorage.RunAsync(fixture, async (client, publicBasePath) =>
        {
            var principalId = TestIds.NewSystemId("sys-alter-avatar", maxLen: 24);

            var alterId = await CreateAlterAsync(client, principalId, "AvatarTarget", "avatar-parity", VisibilityLevel.Public);

            using var uploadRequest = BuildMultipartUploadRequest(client, $"/api/systems/me/alters/{alterId}/avatar", principalId, "avatar-alter.png", "image/png");
            using var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var publicAlterResponse = await client.SendAuthedGetAsync($"/api/systems/{principalId}/alters/{alterId}", principalId);
            var publicAlter = await publicAlterResponse.ReadEnvelopeAsync<BareAlter>(HttpStatusCode.OK);

            var expectedPrefix = $"{publicBasePath}/{principalId}/{alterId}/";
            using (Assert.Multiple())
            {
                await Assert.That(publicAlter.Data.AvatarUrl).IsNotNull();
                await Assert.That(UrlPathStartsWith(publicAlter.Data.AvatarUrl?.Value, expectedPrefix)).IsTrue();
            }

            using var deleteRes = await client.SendAuthedDeleteAsync($"/api/systems/me/alters/{alterId}/avatar", principalId);
            await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var afterDeleteResponse = await client.SendAuthedGetAsync($"/api/systems/{principalId}/alters/{alterId}", principalId);
            var afterDelete = await afterDeleteResponse.ReadEnvelopeAsync<BareAlter>(HttpStatusCode.OK);

            await Assert.That(afterDelete.Data.AvatarUrl).IsNull();
        });
    }
}

