using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Interfold.Api.Models;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure;
using Interfold.IntegrationTests.TestServices;
using Microsoft.Extensions.Options;
using TUnit.Core.Services;

namespace Interfold.IntegrationTests;

public class BaseEndpointTest
{
    private const int SoakRepeatCount = 5;

    /// <summary>
    /// Drives <see cref="RequiredFixtures.Discover"/> from the earliest TUnit hook that has
    /// the scheduled-test set populated, so <c>SharedDbFixture.BuildArgs</c> sees precise
    /// <see cref="RequiredFixtures.NeedScylla"/> / <see cref="RequiredFixtures.NeedCassandra"/>
    /// values reflecting the actual filtered run rather than the entire assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// We use <c>[After(HookType.TestDiscovery)]</c> — a lifecycle probe confirmed both
    /// <see cref="TUnit.Core.TestSessionContext.Current"/> and
    /// <see cref="TUnit.Core.TestDiscoveryContext.Current"/> expose the filter-aware
    /// <c>AllTests</c> list by the time this hook fires. <c>PerTestSession</c>
    /// <c>[ClassDataSource]</c> initialization (the moment <c>SharedDbFixture.Args</c> is
    /// evaluated) still happens during session bootstrap, ahead of
    /// <c>[Before(HookType.TestSession)]</c>, but it runs strictly after this hook so the
    /// scheduled-test contexts are populated before any fixture's <c>InitializeAsync</c>
    /// reads them.
    /// </para>
    /// <para>
    /// Hosted on this base class (every integration test derives from it) so TUnit's source
    /// generator picks up the hook — hooks on static helper classes outside the test graph
    /// are not scanned.
    /// </para>
    /// </remarks>
    [After(HookType.TestDiscovery)]
    public static void RegisterRequiredFixtures()
    {
        LifecycleProbe.Log("After(TestDiscovery)");
        RequiredFixtures.Discover();
    }

    /// <summary>
    /// Lifecycle-ordering probe — temporary. Removed in a follow-up cleanup commit once we've
    /// validated end-to-end that <c>SharedDbFixture.BuildArgs</c> sees populated contexts at
    /// the precise moment its <c>Args</c> getter runs.
    /// </summary>
    [Before(HookType.TestDiscovery)]
    public static void Probe_BeforeTestDiscovery()
        => LifecycleProbe.Log("Before(TestDiscovery)");

    [Before(HookType.TestSession)]
    public static void Probe_BeforeTestSession()
        => LifecycleProbe.Log("Before(TestSession)");

    [After(HookType.TestSession)]
    public static void Probe_AfterTestSession()
        => LifecycleProbe.Log("After(TestSession)");

    // -----------------------------------------------------------------------
    // Wire-shape-agnostic helpers (kept)
    //
    // These helpers do not assume anything about the JSON envelope shape or the
    // typed contract records, so they stay useful even after the sweep.
    // -----------------------------------------------------------------------

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

    /// <summary>
    /// Parses the trailing integer segment from a <c>Location</c> header. The alter-create
    /// controller stamps a URL like <c>/api/systems/me/alters/{alterId}</c> and callers
    /// often need the raw int (e.g. to feed it into a subsequent multipart upload path).
    /// Prefer <see cref="ReadTrailingAlterIdFromLocation"/> when the caller wants the
    /// wrapper type.
    /// </summary>
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

    /// <summary>Same as <see cref="ReadTrailingIntFromLocation"/> but returns the typed wrapper.</summary>
    internal static AlterId ReadTrailingAlterIdFromLocation(HttpResponseMessage response)
        => new((short)ReadTrailingIntFromLocation(response));

    // -----------------------------------------------------------------------
    // Typed request helpers (Step 2 of the strong-typing sweep)
    //
    // Every helper below constructs a real request record from
    // Interfold.Contracts.Models.Read and unpacks a SuccessResponse<T> from the
    // response, so a wire-field typo (missing property, wrong wrapper struct)
    // fails to compile rather than silently returning a bogus body downstream.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Seeds the four-viewer visibility quartet used by every "public read gated by relationship"
    /// integration test (`AltersController` field-visibility, `PublicSystemsController` alter/tag/
    /// fronting visibility, etc.). Every quartet-shaped test previously hand-rolled the same
    /// eight setup lines with slight drift:
    /// <list type="bullet">
    ///   <item>3 × <see cref="CreateAlterAsync"/> for the three viewer principals (a seed alter
    ///         per viewer forces the row into existence — without it, `_alters.ListGuardedAsync`
    ///         short-circuits before the relationship gate ever runs).</item>
    ///   <item>4 × <see cref="EnsureUserExistsAsync"/> so every principal has a public profile
    ///         and the friend-request / trust flow has both ends of the edge to point at.</item>
    /// </list>
    /// <para>
    /// Deliberately unifies to the "seed all four" shape rather than the previous
    /// <c>AltersControllerTests</c> shape (which only seeded the owner). Skipping the viewer
    /// user-exists seeds masks a class of test-side race where the friend-request accept below
    /// tries to resolve a viewer id that has no profile row yet.
    /// </para>
    /// </summary>
    /// <param name="client">Authed HTTP client (typically <c>TestClient.NoRedirect(fixture)</c>).</param>
    /// <param name="prefix">Short slug baked into every principal id and username so parallel
    /// tests don't collide (e.g. <c>"alters-visibility"</c> → <c>alters-visibility-owner</c>,
    /// <c>alters-visibility-nonfriend</c>, …).</param>
    /// <returns>The four principal ids: <c>Owner</c>, <c>NonFriend</c>, <c>Friend</c>, <c>Trusted</c>.</returns>
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

    /// <summary>
    /// Ensures a public profile exists for <paramref name="principal"/> by issuing a username
    /// update. Required for endpoints that gate access on <c>GetPublicProfileAsync</c>
    /// returning non-null. Sends <see cref="SettingsUsernameRequest"/>; the API accepts
    /// either 204 (fresh insert) or 409 (already set) as success.
    /// </summary>
    internal static async Task EnsureUserExistsAsync(HttpClient client, string principal, string? username = null)
    {
        var body = new SettingsUsernameRequest(new Username(username ?? principal));
        using var res = await client.SendAsJsonAsync(HttpMethod.Post, "/api/settings/username", body, principal);
        await Assert.That(res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.Conflict)
            .IsTrue().Because($"EnsureUserExistsAsync failed for '{principal}': {(int)res.StatusCode}");
    }

    /// <summary>
    /// Creates an alter and returns its typed <see cref="AlterId"/>. Reads the response as
    /// <see cref="SuccessResponse{T}"/> over <see cref="AlterReadModel"/> — the same shape
    /// <c>AltersController.Create</c> serialises, so the test picks up any read-model
    /// contract drift at deserialisation time.
    /// </summary>
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

    /// <summary>
    /// Generic PATCH helper against <c>/api/systems/me/alters/{id}</c>. Callers that only
    /// need one field flip through <see cref="SetAlterSecurityLevelAsync"/> /
    /// <see cref="UpdateAlterFieldsAsync"/>; anything more elaborate constructs its own
    /// <see cref="UpdateAlterRequest"/> and calls this directly.
    /// </summary>
    internal static async Task PatchAlterAsync(HttpClient client, string principal, AlterId alterId, UpdateAlterRequest request)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Patch, $"/api/systems/me/alters/{alterId}",
            request,
            principal);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected alter update 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Patches the alter's custom-field values. Replaces the pre-sweep <c>dynamic[] fields</c>
    /// with a typed <see cref="UpdateAlterFieldRequest"/> list so a field id typo (or a
    /// name/value shape drift) fails to compile.
    /// </summary>
    internal static Task UpdateAlterFieldsAsync(HttpClient client, string principal, AlterId alterId, IReadOnlyList<UpdateAlterFieldRequest> fields)
        => PatchAlterAsync(client, principal, alterId, new UpdateAlterRequest(Fields: fields));

    /// <summary>
    /// Sets the alter's <see cref="VisibilityLevel"/> via PATCH. Takes the typed enum so
    /// the wire spelling (public / friends_only / trusted_only / private) is decided by
    /// the converter, not by test-side string literals.
    /// </summary>
    internal static Task SetAlterSecurityLevelAsync(HttpClient client, string principal, AlterId alterId, VisibilityLevel securityLevel)
        => PatchAlterAsync(client, principal, alterId, new UpdateAlterRequest(SecurityLevel: securityLevel));

    /// <summary>
    /// Creates a tag and returns its typed <see cref="TagId"/>. Reads the response as
    /// <see cref="SuccessResponse{T}"/> over <see cref="TagReadModel"/>.
    /// </summary>
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

    /// <summary>
    /// POSTs to <c>/api/systems/me/front/start</c> with a typed <see cref="FrontStartRequest"/>
    /// and asserts a 201. Returns the fresh <see cref="FrontId"/> from
    /// <see cref="FrontStartedResponse"/>.
    /// </summary>
    internal static async Task<FrontId> StartFrontAsync(HttpClient client, string principal, AlterId alterId)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/systems/me/front/start",
            new FrontStartRequest(alterId),
            principal);
        var envelope = await res.ReadEnvelopeAsync<FrontStartedResponse>(HttpStatusCode.Created);
        return envelope.Data.FrontId;
    }

    /// <summary>
    /// Sends the front-start request without asserting the status, so tests can inspect
    /// both the code and the (typed) body. Callers dispose the returned response and use
    /// <c>ReadEnvelopeAsync&lt;FrontStartedResponse&gt;</c> when they need the id.
    /// </summary>
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

    /// <summary>
    /// Creates a settings field and returns its typed <see cref="FieldId"/>. Takes typed
    /// <see cref="FieldType"/> / <see cref="VisibilityLevel"/> parameters — the wire enums
    /// are decided by the converter, not by test-side string literals.
    /// </summary>
    internal static async Task<FieldId> CreateSettingsFieldAsync(HttpClient client, string principal, string fieldName, FieldType type, VisibilityLevel securityLevel)
    {
        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/fields",
            new SettingsCreateFieldRequest(fieldName, type, securityLevel, Locked: null),
            principal);
        var envelope = await res.ReadEnvelopeAsync<FieldCreatedResponse>(HttpStatusCode.Created);
        return envelope.Data.Id;
    }

    /// <summary>
    /// Loops <see cref="SoakRepeatCount"/> calls against the same idempotency key,
    /// asserting that iteration 0 sees <c>replay=false</c> and every subsequent iteration
    /// sees <c>replay=true</c>. Non-empty bodies are deserialised through
    /// <see cref="SuccessResponse{T}"/> over <see cref="JsonElement"/> so the assertion
    /// runs against the typed envelope, not a hand-rolled JSON navigator.
    /// </summary>
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
        // See InterfoldWebApplicationFactory.CreateToken for the IOptionsMonitor vs
        // IConfiguration.Get<T>() rationale — we want the AuthenticationSecretsPostConfigure-
        // patched (cached-in-monitor) instance, not a fresh binding from IConfiguration.
        var authConfig = factory.Services.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>().CurrentValue;

        // Mirror the fixture-side seed of the ES256 keypair so the JWT we issue here verifies
        // against the API's SecretsPreBuildLoader-primed configuration on the server side.
        authConfig.JwtEs256PrivateKeyPem = TestDbCredentials.JwtEs256PrivateKeyPem;

        var jti = Guid.NewGuid().ToString("N");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(1);

        // Every JWT reaching an Interfold controller must carry a scoped `{region}:{rawId}`
        // sub — a raw-sub token 401s at the middleware, the command handler never runs,
        // the event bus never publishes, and the pump-side push the test is waiting for
        // never arrives (surfaces as a WebSocket timeout). Compose is idempotent, so
        // callers that already pass a scoped id get the same value out. Nam is the
        // default region the InMemory bootstrapper seeds, matching the sibling token
        // minter InterfoldWebApplicationFactory.CreateToken.
        var scoped = ScopedSystemId.Compose(ScyllaKeyspace.Nam, systemId);
        var scopedSystemId = scoped.AsSystemId();

        var token = AuthHelper.CreateToken(authConfig, expiresAt, now, new Jti(jti), scopedSystemId);
        // Record the same scoped id so any audit column persisted alongside the revocation
        // row is byte-identical to the JWT sub — future readers cross-referencing revocation
        // by system id see the wire-canonical shape rather than a raw-id ghost.
        await rev.RecordTokenAsync(new Jti(jti), scopedSystemId, expiresAt, CancellationToken.None);

        // EnsureUserExistsAsync goes through AttachPrincipalAuth → factory.CreateToken,
        // which auto-scopes internally, so passing the raw `systemId` here is correct:
        // the user row is keyed by principal identity, and the auth helper handles the
        // wire shape.
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
