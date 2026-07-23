using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Interfold.Api.Models;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure;
using Interfold.IntegrationTests.TestServices;
using Microsoft.Extensions.Options;
using TUnit.Core.Services;

namespace Interfold.IntegrationTests;

public class BaseEndpointTest
{
    private const int SoakRepeatCount = 5;

    /// <summary>Discovers required fixtures before any [ClassDataSource] fixture initializes
    /// so <see cref="SharedDbFixture"/> sees the filtered run rather than the whole assembly.
    /// Hosted here because TUnit only scans hooks on classes reachable from the test graph.</summary>
    [After(HookType.TestDiscovery)]
    public static void RegisterRequiredFixtures()
    {
        LifecycleProbe.Log("After(TestDiscovery)");
        RequiredFixtures.Discover();
    }

    /// <summary>Lifecycle-ordering probe; temporary.</summary>
    [Before(HookType.TestDiscovery)]
    public static void Probe_BeforeTestDiscovery()
        => LifecycleProbe.Log("Before(TestDiscovery)");

    [Before(HookType.TestSession)]
    public static void Probe_BeforeTestSession()
        => LifecycleProbe.Log("Before(TestSession)");

    [After(HookType.TestSession)]
    public static void Probe_AfterTestSession()
        => LifecycleProbe.Log("After(TestSession)");

    internal static byte[] Base64UrlDecodeBytes(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    internal static bool UrlPathStartsWith(string? url, string expectedPrefix)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(expectedPrefix))
            return false;

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.AbsolutePath.StartsWith(expectedPrefix, StringComparison.Ordinal);

        return url.StartsWith(expectedPrefix, StringComparison.Ordinal);
    }

    internal static HttpRequestMessage BuildMultipartUploadRequest(HttpClient client, string path, string principalId, string fileName, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path);
        AttachPrincipalAuth(request, client, principalId);
        request.Headers.Add("X-Interfold-Idempotency-Key", Guid.NewGuid().ToString("N"));

        var data = Encoding.UTF8.GetBytes("octocon-avatar-bytes");
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", fileName);

        request.Content = form;

        return request;
    }

    /// <summary>Parses the trailing integer from a <c>Location</c> header. Prefer
    /// <see cref="ReadTrailingAlterIdFromLocation"/> when a typed wrapper is needed.</summary>
    internal static int ReadTrailingIntFromLocation(HttpResponseMessage response)
    {
        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location))
            throw new InvalidOperationException("Expected Location header on alter-create response.");

        var segment = location.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!int.TryParse(segment, out var id) || id <= 0)
            throw new InvalidOperationException($"Could not parse alter id from Location header '{location}'.");

        return id;
    }

    /// <summary>Typed wrapper form of <see cref="ReadTrailingIntFromLocation"/>.</summary>
    internal static AlterId ReadTrailingAlterIdFromLocation(HttpResponseMessage response)
        => new((short)ReadTrailingIntFromLocation(response));

    /// <summary>Seeds the four-viewer visibility quartet used by every "public read gated by
    /// relationship" test. All four principals get a public profile row; the three viewers
    /// also get a seed alter so <c>ListGuardedAsync</c> can't short-circuit before the
    /// relationship gate. Returns <c>(Owner, NonFriend, Friend, Trusted)</c>.</summary>
    internal static async Task<(string Owner, string NonFriend, string Friend, string Trusted)>
        SeedVisibilityQuartetAsync(HttpClient client, string prefix)
    {
        var owner = $"{prefix}-owner";
        var nonFriend = $"{prefix}-nonfriend";
        var friend = $"{prefix}-friend";
        var trusted = $"{prefix}-trusted";

        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");
        await EnsureUserExistsAsync(client, owner);
        await EnsureUserExistsAsync(client, nonFriend);
        await EnsureUserExistsAsync(client, friend);
        await EnsureUserExistsAsync(client, trusted);

        return (owner, nonFriend, friend, trusted);
    }

    /// <summary>Ensures a public profile exists via a username POST; treats 204 and 409
    /// (already set) as success.</summary>
    internal static async Task EnsureUserExistsAsync(HttpClient client, string principal, string? username = null)
    {
        var body = new SettingsUsernameRequest(new Username(username ?? principal));
        using var res = await client.SendAsJsonAsync(HttpMethod.Post, "/api/settings/username", body, principal);
        await Assert.That(res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.Conflict)
            .IsTrue().Because($"EnsureUserExistsAsync failed for '{principal}': {(int)res.StatusCode}");
    }

    /// <summary>Creates an alter and returns its typed <see cref="AlterId"/>. Reads the
    /// response as <see cref="SuccessResponse{T}"/> over <see cref="AlterReadModel"/> so
    /// read-model drift surfaces at deserialisation.</summary>
    internal static async Task<AlterId> CreateAlterAsync(HttpClient client, string principal, string name, VisibilityLevel visibility = VisibilityLevel.Public)
    {
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/alters",
            new CreateAlterRequest(name),
            principal);
        var envelope = await res.ReadEnvelopeAsync<AlterReadModel>(HttpStatusCode.Created);
        
        if (visibility != VisibilityLevel.Private)
        {
            await SetAlterSecurityLevelAsync(client, principal, envelope.Data.Id, visibility);
        }
        
        return envelope.Data.Id;
    }

    internal static async Task<AlterId> CreateAlterAsync(HttpClient client, string principal, string name, string username, VisibilityLevel visibility = VisibilityLevel.Public)
    {
        await EnsureUserExistsAsync(client, principal, username);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/alters",
            new CreateAlterRequest(name),
            principal);
        var envelope = await res.ReadEnvelopeAsync<AlterReadModel>(HttpStatusCode.Created);
        
        if (visibility != VisibilityLevel.Private)
        {
            await SetAlterSecurityLevelAsync(client, principal, envelope.Data.Id, visibility);
        }
        
        return envelope.Data.Id;
    }

    /// <summary>Generic PATCH against <c>/api/systems/me/alters/{id}</c>.</summary>
    internal static async Task PatchAlterAsync(HttpClient client, string principal, AlterId alterId, UpdateAlterRequest request)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Patch, $"/api/systems/me/alters/{alterId}",
            request,
            principal);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected alter update 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    /// <summary>Patches the alter's custom-field values via typed
    /// <see cref="UpdateAlterFieldRequest"/> list.</summary>
    internal static Task UpdateAlterFieldsAsync(HttpClient client, string principal, AlterId alterId, IReadOnlyList<UpdateAlterFieldRequest> fields)
        => PatchAlterAsync(client, principal, alterId, new UpdateAlterRequest(Fields: fields));

    /// <summary>Sets the alter's <see cref="VisibilityLevel"/> via PATCH.</summary>
    internal static Task SetAlterSecurityLevelAsync(HttpClient client, string principal, AlterId alterId, VisibilityLevel securityLevel)
        => PatchAlterAsync(client, principal, alterId, new UpdateAlterRequest(SecurityLevel: securityLevel));

    /// <summary>Creates a tag and returns its typed <see cref="TagId"/>.</summary>
    internal static async Task<TagId> CreateTagAsync(HttpClient client, string principal, string name)
    {
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/tags",
            new CreateTagRequest(name, ParentTagId: null),
            principal);
        var envelope = await res.ReadEnvelopeAsync<TagReadModel>(HttpStatusCode.Created);
        return envelope.Data.Id;
    }

    /// <summary>Sets a tag's <see cref="VisibilityLevel"/> via PATCH.</summary>
    internal static async Task SetTagSecurityLevelAsync(HttpClient client, string principal, TagId tagId, VisibilityLevel securityLevel)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Patch, $"/api/systems/me/tags/{tagId}",
            new UpdateTagRequest(SecurityLevel: securityLevel),
            principal);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected tag security update 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    /// <summary>POSTs a typed <see cref="FrontStartRequest"/>, asserts 201, returns the new
    /// <see cref="FrontId"/>.</summary>
    internal static async Task<FrontId> StartFrontAsync(HttpClient client, string principal, AlterId alterId)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/front/start",
            new FrontStartRequest(alterId),
            principal);
        var envelope = await res.ReadEnvelopeAsync<FrontStartedResponse>(HttpStatusCode.Created);
        return envelope.Data.FrontId;
    }

    /// <summary>Sends front-start without asserting status; callers dispose and can read
    /// the typed envelope themselves.</summary>
    internal static Task<HttpResponseMessage> SendFrontStartAsync(
        HttpClient client,
        AlterId alterId,
        string? comment,
        string principal = "fronting-default-principal")
        => client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/front/start",
            new FrontStartRequest(alterId, comment),
            principal,
            idempotencyKey: Guid.NewGuid().ToString("N"));

    internal static Task<HttpResponseMessage> SendFrontEndAsync(
        HttpClient client,
        AlterId alterId,
        string principal = "fronting-default-principal")
        => client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/front/end",
            new FrontEndRequest(alterId),
            principal,
            idempotencyKey: Guid.NewGuid().ToString("N"));

    internal static Task<HttpResponseMessage> SendFrontSetAsync(
        HttpClient client,
        AlterId alterId,
        string principal,
        string? comment = null)
        => client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/front/set",
            new FrontSetRequest(alterId, comment),
            principal,
            idempotencyKey: Guid.NewGuid().ToString("N"));

    internal static async Task SendFriendRequestAndAcceptAsync(HttpClient client, string sender, string recipient)
    {
        using var sendRes = await client.SendAsJsonAsync(
            HttpMethod.Put, $"/api/friend-requests/{recipient}",
            new object(),
            sender);
        await Assert.That(sendRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected friend-request send 204, got {(int)sendRes.StatusCode}. Body: {await sendRes.Content.ReadAsStringAsync()}");

        using var acceptRes = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friend-requests/{sender}/accept",
            new object(),
            recipient);
        await Assert.That(acceptRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected friend-request accept 204, got {(int)acceptRes.StatusCode}. Body: {await acceptRes.Content.ReadAsStringAsync()}");
    }

    internal static async Task SetFriendTrustAsync(HttpClient client, string principal, string friendId)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, $"/api/friends/{friendId}/trust",
            new object(),
            principal);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected trust set 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    /// <summary>Creates a settings field and returns its typed <see cref="FieldId"/>.</summary>
    internal static async Task<FieldId> CreateSettingsFieldAsync(HttpClient client, string principal, string fieldName, FieldType type, VisibilityLevel securityLevel)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/fields",
            new SettingsCreateFieldRequest(fieldName, type, securityLevel, Locked: null),
            principal);
        var envelope = await res.ReadEnvelopeAsync<FieldCreatedResponse>(HttpStatusCode.Created);
        return envelope.Data.Id;
    }

    /// <summary>Loops <see cref="SoakRepeatCount"/> calls against the same idempotency key;
    /// iteration 0 must see replay=false and every later iteration replay=true.</summary>
    internal static async Task RunSoakAsync(InterfoldWebApplicationFactory factory,
        Func<HttpClient, string, Task<HttpResponseMessage>> requestFactory)
    {
        using var client = factory.CreateClient();
        var token = factory.CreateToken("soak-default-principal");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Interfold-Principal");
        client.DefaultRequestHeaders.Add("X-Interfold-Principal", "soak-default-principal");

        await EnsureUserExistsAsync(client, "soak-default-principal");

        var key = Guid.NewGuid().ToString("N");

        for (var i = 0; i < SoakRepeatCount; i++)
        {
            using var response = await requestFactory(client, key);
            var body = await response.Content.ReadAsStringAsync();

            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because($"Soak call #{i + 1}: expected 2xx, got {(int)response.StatusCode}. Body: {body}");

            if (string.IsNullOrEmpty(body))
                continue;

            var envelope = JsonSerializer.Deserialize<TestEnvelope<JsonElement>>(body, TestJson.Options)
                           ?? throw new InvalidOperationException($"Soak call #{i + 1}: failed to deserialise TestEnvelope. Body: {body}");

            var replay = envelope.Replay ?? false;

            if (i == 0)
            {
                await Assert.That(!replay).IsTrue().Because($"Soak call #1: expected replay=false on first invocation. Body: {body}");
            }
            else
            {
                await Assert.That(replay).IsTrue().Because($"Soak call #{i + 1}: expected replay=true after first invocation. Body: {body}");
            }
        }
    }

    internal static async Task<string> CreateRandomToken(InterfoldWebApplicationFactory factory, string systemId)
    {
        var rev = factory.Services.GetRequiredService<IAuthTokenRevocationRepository>();
        // OptionsMonitor.CurrentValue is the PostConfigure-patched instance; a fresh
        // IConfiguration.Get<T>() would bypass the JWT key patches.
        var authConfig = factory.Services.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>().CurrentValue;

        authConfig.JwtEs256PrivateKeyPem = TestDbCredentials.JwtEs256PrivateKeyPem;

        var jti = Guid.NewGuid().ToString("N");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(1);

        // Middleware requires a scoped `{region}:{rawId}` sub; raw-sub 401s and the WS
        // pump never sees the event. Nam matches the InMemory bootstrapper default.
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, systemId);
        var scopedSystemId = scoped.AsSystemId();

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, new Jti(jti), scopedSystemId);
        // Record the scoped id so audit columns match the JWT sub byte-for-byte.
        await rev.RecordTokenAsync(new Jti(jti), scopedSystemId, expiresAt, CancellationToken.None);

        // Raw systemId is fine here: EnsureUserExistsAsync → AttachPrincipalAuth →
        // factory.CreateToken scopes internally.
        using var client = factory.CreateClient();
        await EnsureUserExistsAsync(client, systemId);

        return token;
    }

    internal static void AttachPrincipalAuth(HttpRequestMessage request, HttpClient client, string principal)
    {
        if (!InterfoldWebApplicationFactory.TryGetFactory(client, out var factory))
            throw new InvalidOperationException("Could not resolve test factory for HttpClient. Use InterfoldWebApplicationFactory.CreateClient() to create test clients.");

        var token = factory.CreateToken(principal);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Remove("X-Interfold-Principal");
        request.Headers.Add("X-Interfold-Principal", principal);
    }
}
