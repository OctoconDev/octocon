using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
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

    internal static bool ReadBoolField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(fieldName).GetBoolean();
    }
    
    internal static string ReadStringField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadStringField(doc.RootElement, fieldName);
    }

    internal static string ReadStringField(JsonElement root, string fieldName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString() ?? string.Empty;

            throw new InvalidOperationException(
                $"Expected string field '{fieldName}', got {prop.Value.ValueKind}.");
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found.");
    }

    internal static string? ReadNullableStringField(JsonElement root, string fieldName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                _ => throw new InvalidOperationException(
                    $"Expected nullable string field '{fieldName}', got {prop.Value.ValueKind}.")
            };
        }

        return null;
    }

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
    
    internal static string ReadNestedStringField(string json, string parentField, string childField)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(parentField, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Expected object for field '{parentField}'.");

            foreach (var child in prop.Value.EnumerateObject())
            {
                if (!child.Name.Equals(childField, StringComparison.OrdinalIgnoreCase))
                    continue;

                return child.Value.ValueKind == JsonValueKind.String
                    ? child.Value.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        return string.Empty;
    }
    
    internal static string FindStringContaining(string json, string expectedSubstring)
    {
        using var doc = JsonDocument.Parse(json);
        return FindStringContaining(doc.RootElement, expectedSubstring) ?? string.Empty;
    }

    private static string? FindStringContaining(JsonElement element, string expectedSubstring)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.Contains(expectedSubstring, StringComparison.Ordinal))
                return value;

            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var found = FindStringContaining(property.Value, expectedSubstring);
                if (found is not null)
                    return found;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindStringContaining(item, expectedSubstring);
                if (found is not null)
                    return found;
            }
        }

        return null;
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
    
    internal static string ReadNestedString(string json, string parentKey, string childKey)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(parentKey, StringComparison.OrdinalIgnoreCase) ||
                prop.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var child in prop.Value.EnumerateObject())
            {
                if (child.Name.Equals(childKey, StringComparison.OrdinalIgnoreCase) &&
                    child.Value.ValueKind == JsonValueKind.String)
                    return child.Value.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    internal static bool ReadBool(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind == JsonValueKind.True;
        }
        throw new InvalidOperationException($"Field '{key}' not found in: {json}");
    }
    
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

            if (!string.IsNullOrEmpty(body))
            {
                var replay = ReadBoolField(body, "replay");

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
    }
    
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    internal static async Task UpdateAlterFieldsAsync(HttpClient client, string principal, int alterId, dynamic[] fields)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/me/alters/{alterId}");
        req.Content = JsonContent.Create(new { fields });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because($"Expected alter field update 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }
    
    /// <summary>
    /// Ensures a public profile exists for <paramref name="principal"/> by issuing a username update.
    /// Required for endpoints that gate access on <c>GetPublicProfileAsync</c> returning non-null.
    /// </summary>
    internal static async Task EnsureUserExistsAsync(HttpClient client, string principal, string? username = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username");
        req.Content = JsonContent.Create(new { username = username ?? principal });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        // 204 = accepted, 409 = already set — both are fine.
        await Assert.That(res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.Conflict)
            .IsTrue().Because($"EnsureUserExistsAsync failed for '{principal}': {(int)res.StatusCode}");
    }

    internal static async Task<int> CreateAlterAsync(HttpClient client, string principal, string name)
    {
        await EnsureUserExistsAsync(client, principal);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters");
        req.Content = JsonContent.Create(new { name });
        AttachPrincipalAuth(req, client, principal);

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode == HttpStatusCode.Created).IsTrue().Because($"Helper CreateAlterAsync: expected 201, got {(int)res.StatusCode}. Body: {body}");

        using var doc = JsonDocument.Parse(body);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                prop.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var child in prop.Value.EnumerateObject())
            {
                if ((child.Name.Equals("alterId", StringComparison.OrdinalIgnoreCase) ||
                     child.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) &&
                    child.Value.TryGetInt32(out var id))
                    return id;
            }
        }

        throw new InvalidOperationException($"Could not parse alterId from create response. Body: {body}");
    }

    internal static async Task<string> CreateTagAsync(HttpClient client, string principal, string name)
    {
        await EnsureUserExistsAsync(client, principal);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/tags");
        req.Content = JsonContent.Create(new { name });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode == HttpStatusCode.Created).IsTrue().Because($"Helper CreateTagAsync: expected 201, got {(int)res.StatusCode}. Body: {body}");

        var id = ReadNestedString(body, "data", "tagId");
        if (string.IsNullOrWhiteSpace(id))
            id = ReadNestedString(body, "data", "id");

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Could not parse tag ID from create response. Body: {body}");

        return id;
    }

    internal static async Task SetAlterSecurityLevelAsync(HttpClient client, string principal, int alterId, string securityLevel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/me/alters/{alterId}");
        req.Content = JsonContent.Create(new { security_level = securityLevel });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        await Assert.That(res.StatusCode == HttpStatusCode.NoContent).IsTrue().Because($"Expected alter security update 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    internal static async Task SetTagSecurityLevelAsync(HttpClient client, string principal, string tagId, string securityLevel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/me/tags/{tagId}");
        req.Content = JsonContent.Create(new { security_level = securityLevel });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        await Assert.That(res.StatusCode == HttpStatusCode.NoContent).IsTrue().Because($"Expected tag security update 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    internal static async Task StartFrontAsync(HttpClient client, string principal, int alterId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/start");
        req.Content = JsonContent.Create(new { id = alterId });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        await Assert.That(res.StatusCode == HttpStatusCode.Created).IsTrue().Because($"Expected front start 201, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    internal static async Task SendFriendRequestAndAcceptAsync(HttpClient client, string sender, string recipient)
    {
        using var sendReq = new HttpRequestMessage(HttpMethod.Put, $"/api/friend-requests/{recipient}");
        sendReq.Content = JsonContent.Create(new { });
        AttachPrincipalAuth(sendReq, client, sender);
        var sendRes = await client.SendAsync(sendReq);
        await Assert.That(sendRes.StatusCode == HttpStatusCode.NoContent).IsTrue().Because($"Expected friend-request send 204, got {(int)sendRes.StatusCode}. Body: {await sendRes.Content.ReadAsStringAsync()}");

        using var acceptReq = new HttpRequestMessage(HttpMethod.Post, $"/api/friend-requests/{sender}/accept");
        acceptReq.Content = JsonContent.Create(new { });
        AttachPrincipalAuth(acceptReq, client, recipient);
        var acceptRes = await client.SendAsync(acceptReq);
        await Assert.That(acceptRes.StatusCode == HttpStatusCode.NoContent).IsTrue().Because($"Expected friend-request accept 204, got {(int)acceptRes.StatusCode}. Body: {await acceptRes.Content.ReadAsStringAsync()}");
    }

    internal static async Task SetFriendTrustAsync(HttpClient client, string principal, string friendId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/friends/{friendId}/trust");
        req.Content = JsonContent.Create(new { });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        await Assert.That(res.StatusCode == HttpStatusCode.NoContent).IsTrue().Because($"Expected trust set 204, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    internal static async Task<(HttpStatusCode StatusCode, string Body)> SendFrontStartAsync(
        HttpClient client,
        int alterId,
        string? comment,
        string principal = "fronting-default-principal")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/start");
        request.Content = JsonContent.Create(new
        {
            id = alterId,
            comment,
            idempotencyKey = Guid.NewGuid().ToString("N")
        }, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        AttachPrincipalAuth(request, client, principal);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    internal static async Task<(HttpStatusCode StatusCode, string Body)> SendFrontEndAsync(
        HttpClient client,
        int alterId,
        string principal = "fronting-default-principal")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/end");
        request.Content = JsonContent.Create(new
        {
            id = alterId,
            idempotencyKey = Guid.NewGuid().ToString("N")
        }, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        AttachPrincipalAuth(request, client, principal);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    internal static async Task<(HttpStatusCode StatusCode, string Body)> SendFrontSetAsync(
        HttpClient client,
        int alterId,
        string principal,
        string? comment = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/set");
        request.Content = JsonContent.Create(new
        {
            id = alterId,
            comment,
            idempotencyKey = Guid.NewGuid().ToString("N")
        }, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        AttachPrincipalAuth(request, client, principal);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }
    
    internal static async Task AssertContainsAsync(
        HttpClient client,
        string path,
        string? principal,
        string needle,
        bool expectedPresent,
        HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(principal))
        {
            AttachPrincipalAuth(req, client, principal);
        }

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        var hasNeedle = ContainsNeedle(body, needle);

        using IDisposable _ = Assert.Multiple();
        await Assert.That(res.StatusCode).IsEqualTo(expectedStatus).Because($"Expected {path} to return {(int)expectedStatus}, got {(int)res.StatusCode}. Body: {body}");
        await Assert.That(hasNeedle).IsEquivalentTo(expectedPresent).Because($"Expected body {(expectedPresent ? "to contain" : "not to contain")} '{needle}' for {path}. Body: {body}");
    }

    private static bool ContainsNeedle(string body, string needle)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(needle))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonContainsNeedle(doc.RootElement, needle);
        }
        catch (JsonException)
        {
            // Fallback for non-JSON payloads.
            return body.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool JsonContainsNeedle(JsonElement element, string needle)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (JsonContainsNeedle(property.Value, needle))
                        return true;
                }
                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (JsonContainsNeedle(item, needle))
                        return true;
                }
                return false;

            case JsonValueKind.String:
                return string.Equals(element.GetString(), needle, StringComparison.OrdinalIgnoreCase);

            case JsonValueKind.Number:
                return NumberEqualsNeedle(element, needle);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return bool.TryParse(needle, out var boolNeedle) && element.GetBoolean() == boolNeedle;

            default:
                return false;
        }
    }

    private static bool NumberEqualsNeedle(JsonElement numberElement, string needle)
    {
        if (int.TryParse(needle, out var intNeedle) && numberElement.TryGetInt32(out var intValue))
            return intValue == intNeedle;

        if (long.TryParse(needle, out var longNeedle) && numberElement.TryGetInt64(out var longValue))
            return longValue == longNeedle;

        if (decimal.TryParse(needle, out var decimalNeedle) && numberElement.TryGetDecimal(out var decimalValue))
            return decimalValue == decimalNeedle;

        return false;
    }

    internal static async Task<string> CreateSettingsFieldAsync(HttpClient client, string principal, string fieldName, string type, string securityLevel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields");
        req.Content = JsonContent.Create(new { name = fieldName, type, security_level = securityLevel });
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode == HttpStatusCode.Created).IsTrue().Because($"Expected settings field create 201, got {(int)res.StatusCode}. Body: {body}");

        // Extract field ID from response.
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var idProp))
        {
            return idProp.GetString() ?? throw new InvalidOperationException("Field ID is null");
        }

        throw new InvalidOperationException($"Cannot extract field ID from response body: {body}");
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