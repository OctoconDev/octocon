using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class SettingsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task SettingsField_InvalidType_ReturnsBadRequest()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-fallback";
        await EnsureUserExistsAsync(client, principal);

        // type = "garbage" — the Elixir server used to fold this to "text", but we deliberately
        // hold every enum boundary to the same fail-fast contract EnumWireExtensions documents
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "FallbackField", type = "garbage" })
        };
        AttachPrincipalAuth(req1, client, principal);
        var res1 = await client.SendAsync(req1);

        await Assert.That(res1.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
    
    [Test]
    public async Task SettingsField_MissingType_FallsBackToText_ReturnsCreatedWithId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-missing-type";
        await EnsureUserExistsAsync(client, principal);

        // type absent entirely
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "NoTypeField" })
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadNestedString(body, "data", "id")).IsNotNullOrWhiteSpace();
        }
    }
    
    [Test]
    public async Task Idempotency_SettingsUsernameUpdate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "soakuser" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }
    
    [Test]
    public async Task SettingsField_Create_ReturnsCreatedFieldId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-defaults";
        await EnsureUserExistsAsync(client, principal);

        // Create field without explicit security_level.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "DefaultSecurityField", type = "text" })
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        var fieldId = ReadNestedString(body, "data", "id");

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(fieldId).IsNotNullOrWhiteSpace();
        }
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
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        // AvatarPublicBase is validated by [AbsoluteHttpUri] on StorageConfiguration, so
        // the configured value must be an absolute http(s) URL. WebApplicationFactory's
        // default BaseAddress is http://localhost/, so "http://localhost" matches request
        // origin resolution downstream. publicBasePath is what we assert against, because
        // UrlPathStartsWith compares against Uri.AbsolutePath (the path, not the full URL).
        var publicBasePath = $"/avatars-itest/{runId}";
        var publicBase = $"http://localhost{publicBasePath}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            await using var isolatedFactory = fixture.CreatePrivateFactory()
                .WithConfiguration("OCTOCON_AVATAR_STORAGE_ROOT", storageRoot)
                .WithConfiguration("OCTOCON_AVATAR_PUBLIC_BASE", publicBase);

            using var client = isolatedFactory.CreateClient();

            var principalId = $"sys-avatar-{Guid.NewGuid():N}"[..18];

            using var uploadRequest = BuildMultipartUploadRequest(client, "/api/settings/avatar", principalId, "avatar-system.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}");
            AttachPrincipalAuth(profileRequest, client, principalId);
            var profileResponse = await client.SendAsync(profileRequest);
            var profileBody = await profileResponse.Content.ReadAsStringAsync();

            var avatarUrl = ReadNestedStringField(profileBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(profileResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(avatarUrl).IsNotNullOrWhiteSpace();
                await Assert.That(UrlPathStartsWith(avatarUrl, $"{publicBasePath}/{principalId}/self/")).IsTrue();
            }

            // End-to-end serving check: the avatar_url that the SPA receives from the
            // profile response must actually return the bytes when fetched. Before the
            // Phase-2 middleware in Program.cs this would 404 — the API wrote the file
            // to disk and stamped a URL, but no handler served the bytes back. The
            // assertion below is the regression guard for that "URL stamped but no
            // listener" gap. We do it inside the same test so any future shape change
            // to LocalAvatarStorage's URL format and the middleware's path matching is
            // caught in one place.
            using var avatarRequest = new HttpRequestMessage(HttpMethod.Get, avatarUrl);
            // Avatar GETs are unauthenticated (the URL itself is the capability — this
            // is the same exposure model as a CDN-fronted setup). Anonymous request is
            // intentional so the assertion reflects what the SPA / external embed sees.
            var avatarResponse = await client.SendAsync(avatarRequest);
            var avatarBytes = await avatarResponse.Content.ReadAsByteArrayAsync();
            using (Assert.Multiple())
            {
                await Assert.That(avatarResponse.StatusCode).IsEqualTo(HttpStatusCode.OK)
                    .Because($"Expected the avatar URL returned by the profile endpoint to be servable. URL was '{avatarUrl}'.");
                await Assert.That(avatarBytes.Length).IsGreaterThan(0)
                    .Because("Expected non-zero bytes back from the avatar GET — a zero-length response would indicate the middleware matched the path but couldn't read the file off disk.");
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }

    // Isolation contract: see the sibling Api_SettingsAvatarMultipart_PersistsAndServesAvatar
    // above for the full rationale — this test also builds its OWN factory via
    // CreatePrivateFactory so its avatar-storage config mutations never leak into
    // fixture.Factory and cannot poison other tests in the session.
    [Test, NotInParallel("avatar-storage-config")]
    public async Task Api_AlterAvatarMultipart_PersistsAndReflectsOnPublicAlter()
    {
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        // See sibling test for the AbsoluteHttpUri constraint on AvatarPublicBase and the
        // path-vs-URL split. publicBasePath is what we assert against because
        // UrlPathStartsWith compares Uri.AbsolutePath (the path portion) not the full URL.
        var publicBasePath = $"/avatars-itest/{runId}";
        var publicBase = $"http://localhost{publicBasePath}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            await using var isolatedFactory = fixture.CreatePrivateFactory()
                .WithConfiguration("OCTOCON_AVATAR_STORAGE_ROOT", storageRoot)
                .WithConfiguration("OCTOCON_AVATAR_PUBLIC_BASE", publicBase);

            using var client = isolatedFactory.CreateClient();

            var principalId = $"sys-alter-avatar-{Guid.NewGuid():N}"[..24];

            using var usernameRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "avatar-parity" })
            };
            AttachPrincipalAuth(usernameRequest, client, principalId);
            var usernameResponse = await client.SendAsync(usernameRequest);
            await Assert.That(usernameResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
            {
                Content = JsonContent.Create(new { name = "AvatarTarget" })
            };
            AttachPrincipalAuth(createRequest, client, principalId);

            var createResponse = await client.SendAsync(createRequest);
            await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);

            var alterId = ReadTrailingIntFromLocation(createResponse);

            using var uploadRequest = BuildMultipartUploadRequest(client, $"/api/systems/me/alters/{alterId}/avatar", principalId, "avatar-alter.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var publicAlterRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}/alters/{alterId}");
            AttachPrincipalAuth(publicAlterRequest, client, principalId);
            var publicAlterResponse = await client.SendAsync(publicAlterRequest);
            var publicAlterBody = await publicAlterResponse.Content.ReadAsStringAsync();

            var expectedPrefix = $"{publicBasePath}/{principalId}/{alterId}/";
            var alterAvatarUrl = ReadNestedStringField(publicAlterBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(publicAlterResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(alterAvatarUrl).IsNotNullOrWhiteSpace();
                await Assert.That(UrlPathStartsWith(alterAvatarUrl, expectedPrefix)).IsTrue();
            }

            using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/{alterId}/avatar")
            {
                Content = JsonContent.Create(new { })
            };
            AttachPrincipalAuth(deleteReq, client, principalId);
            var deleteRes = await client.SendAsync(deleteReq);
            await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var afterDeleteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}/alters/{alterId}");
            AttachPrincipalAuth(afterDeleteRequest, client, principalId);
            var afterDeleteResponse = await client.SendAsync(afterDeleteRequest);
            var afterDeleteBody = await afterDeleteResponse.Content.ReadAsStringAsync();

            var staleAvatarUrl = ReadNestedStringField(afterDeleteBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(afterDeleteResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(staleAvatarUrl).IsNullOrWhiteSpace();
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }
}