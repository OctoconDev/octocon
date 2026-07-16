using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.Contracts.Configuration;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for authentication, OAuth flows, and WebSocket upgrades.
/// Refactored to use WebApplicationFactory for in-memory testing.
/// </summary>
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task Api_AuthRequest_FallsBackTo403_WhenChallengeDisabled()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/auth/google");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    //TODO: DO we need this? Maybe can split it up another way...
    [Test]
    public async Task Api_AuthAndIdempotencyFlow_VerifiesEndToEndBehavior()
    {
        using var client = fixture.Factory.CreateClient();
        var principalId = "sys-api-smoke";

        // Unauthorized check
        var unauthorized = await client.PostAsJsonAsync("/api/systems/me/alters", new { name = "NoPrincipal" });
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // First creation
        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "IntegrationOne" })
        };
        AttachPrincipalAuth(firstReq, client, principalId);
        firstReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var firstRes = await client.SendAsync(firstReq);
        var firstBody = await firstRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(firstRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadBoolField(firstBody, "replay")).IsFalse();
        }

        // Replay check
        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "IntegrationOne" })
        };
        AttachPrincipalAuth(secondReq, client, principalId);
        secondReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var secondRes = await client.SendAsync(secondReq);
        var secondBody = await secondRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(secondRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadBoolField(secondBody, "replay")).IsTrue();
        }

        // Verification of list
        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/systems/me/alters");
        AttachPrincipalAuth(listReq, client, principalId);
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();
        
        using var listDoc = JsonDocument.Parse(listBody);
        var altersData = listDoc.RootElement.GetProperty("data");
        using (Assert.Multiple())
        {
            await Assert.That(listRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(altersData.GetArrayLength()).IsGreaterThan(0);
        }
    }


    [Test]
    public async Task Api_OAuthCallback_IssuesJwsCompactSerializationToken()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // The callback now strictly requires a client-supplied redirect_uri (stashed by the
        // initial GET /auth/discord call into octocon_auth_redirect_uri). Since this test
        // hits the callback directly without going through the begin step, inject the cookie
        // by hand so the controller can construct a Location header — the JWT-shape
        // assertions below are the actual unit under test.
        var discordUid = $"jws-test-{Guid.NewGuid():N}";
        var clientRedirectUri = "https://test.invalid/auth/done";
        var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/discord/callback?uid={Uri.EscapeDataString(discordUid)}");
        callbackRequest.Headers.Add("Cookie",
            $"octocon_auth_redirect_uri={Uri.EscapeDataString(clientRedirectUri)}");
        var response = await client.SendAsync(callbackRequest);
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode)
            .Satisfies(x => x is HttpStatusCode.Redirect or HttpStatusCode.Found
        or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
            .Because($"Expected OAuth callback redirect response, got {(int)response.StatusCode}. Body: {body}");

        var location = response.Headers.Location
            ?? throw new InvalidOperationException("Expected Location header in redirect response.");

        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        var token = query["token"];

        await Assert.That(token).IsNotNullOrWhiteSpace();

        var segments = token!.Split('.');
        await Assert.That(segments.Length).IsEqualTo(3);

        using (Assert.Multiple())
        {
            foreach (var (segment, index) in segments.Select((s, i) => (s, i)))
            {
                await Assert.That(segment).IsNotNullOrWhiteSpace();
                await Assert.That(!segment.Contains('+') && !segment.Contains('/') && !segment.Contains('=')).IsTrue();
            }
        }

        var headerBytes = Base64UrlDecodeBytes(segments[0]);
        using var headerDoc = JsonDocument.Parse(headerBytes);
        var alg = headerDoc.RootElement.GetProperty("alg").GetString();
        var typ = headerDoc.RootElement.GetProperty("typ").GetString();
        using (Assert.Multiple())
        {
            await Assert.That(alg).IsEqualTo("ES256");
            await Assert.That(typ).IsEqualTo("JWT");
        }

        var payloadBytes = Base64UrlDecodeBytes(segments[1]);
        using var payloadDoc = JsonDocument.Parse(payloadBytes);
        var root = payloadDoc.RootElement;

        var authConfig = fixture.Factory.Services.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>().CurrentValue;
        var expectedIssuer = authConfig.JwtAuthority;

        using (Assert.Multiple())
        {
            await Assert.That(
                root.TryGetProperty("iss", out var iss)
                && !string.IsNullOrWhiteSpace(iss.GetString())
                && (string.IsNullOrWhiteSpace(expectedIssuer)
                    || string.Equals(iss.GetString(), expectedIssuer, StringComparison.Ordinal)))
                .IsTrue();
            await Assert.That(root.TryGetProperty("sub", out var sub) && !string.IsNullOrWhiteSpace(sub.GetString())).IsTrue();
        }

        long iatVal = 0;
        long expVal = 0;

        using (Assert.Multiple())
        {
            await Assert.That(root.TryGetProperty("iat", out var iat) && iat.TryGetInt64(out iatVal) && iatVal > 0).IsTrue();
            await Assert.That(root.TryGetProperty("nbf", out var nbf) && nbf.TryGetInt64(out _)).IsTrue();
            await Assert.That(root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out expVal) && expVal > iatVal).IsTrue();
            await Assert.That(root.TryGetProperty("jti", out var jti) && !string.IsNullOrWhiteSpace(jti.GetString())).IsTrue();
            await Assert.That(root.TryGetProperty("scope", out var scope) &&
                   string.Equals(scope.GetString(), "octocon:deeplink", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test, Skip("To readd")]
    public async Task Api_FailsFast_WithoutJwtAuthority_WhenDevHeaderBypassOff()
    {
        using var client = fixture.Factory.CreateClient();

        //TODO: Readd
        /*var exited = await WaitForExitAsync(process, timeoutMs: 12000);
        Ensure(exited, "Expected API process to fail fast, but it did not exit in time.");
        Ensure(process.ExitCode != 0, "Expected non-zero exit code when JWT authority is missing with dev bypass off.");

        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var combined = string.Concat(stdout, "\n", stderr);

        Ensure(combined.Contains("OCTOCON_JWT_AUTHORITY", StringComparison.Ordinal),
            $"Expected startup guardrail message mentioning OCTOCON_JWT_AUTHORITY. Output: {combined}");*/
    }
}
