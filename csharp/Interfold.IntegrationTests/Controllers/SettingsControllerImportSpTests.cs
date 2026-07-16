using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.Contracts.Events;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// End-to-end tests for the async-import dispatch endpoint. The controller layer is the
/// one piece the unit tests cannot exercise on their own — they cover the handler, the
/// repository, the queue, and the worker in isolation, but not the HTTP plumbing
/// (envelope construction from <see cref="HttpRequestMessage"/>, <c>CommandAccepted</c>
/// status mapping, body serialisation of <c>ImportDispatchResponse</c>).
///
/// <para>
/// We run against the in-memory fixture only. Cassandra and Scylla variants would
/// re-test the same controller code path with no additional coverage and would slow the
/// CI suite measurably. The repository-port equivalence is pinned separately by
/// <c>InMemoryImportOperationRepositoryTests</c> and a future
/// <c>ScyllaImportOperationRepositoryTests</c> integration.
/// </para>
///
/// <para>
/// <b>Side-effect note.</b> The real <c>SpImportJobRunner</c> registered in
/// <c>Program.cs</c> resolves to <c>SimplyPluralImportService</c>, which would normally
/// hit <c>https://api.apparyllis.com/v1</c>. In the test factory all HTTP clients are
/// built from <c>TestHttpClientFactory.CreateDefaultClient()</c>, which dispatches by
/// path through TestServer — the absolute Apparyllis URL resolves to a missing route
/// inside our own API, the service throws / returns a graceful failure, and the worker
/// publishes <see cref="SimplyPluralImportFailedEvent"/>. This is intentional: it lets
/// us assert the full dispatch -> worker -> bus path end-to-end without making an
/// outbound network call.
/// </para>
/// </summary>
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class SettingsControllerImportSpTests(InMemoryWebFactoryFixture fixture) : BaseEndpointTest
{
    /// <summary>
    /// POST <c>/api/settings/import-sp</c> must return HTTP 202 Accepted with an
    /// <c>ImportDispatchResponse</c> body carrying a non-empty operation_id and
    /// <c>status = "queued"</c>. This is the load-bearing controller contract: the
    /// Compose app ignores the body on success but a future client (or operator log)
    /// uses the operation_id as the correlation handle, and the 202 itself is what
    /// keeps the response off the synchronous retry path that produced the original
    /// duplicate-import bug.
    /// </summary>
    [Test]
    public async Task ImportSp_FreshDispatch_Returns202WithOperationId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var principal = $"sp-import-202-{Guid.NewGuid():N}"[..24];
        await EnsureUserExistsAsync(client, principal);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/import-sp")
        {
            Content = JsonContent.Create(new { token = "synthetic-sp-token" }),
        };
        AttachPrincipalAuth(req, client, principal);

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Accepted)
                .Because($"Expected 202 Accepted for the async dispatch (the load-bearing change of Phase 3b), got {(int)res.StatusCode}. Body: {body}");

            var operationId = ReadNestedString(body, "data", "operation_id");
            var status = ReadNestedString(body, "data", "status");
            await Assert.That(operationId).IsNotNullOrWhiteSpace()
                .Because("The response body must carry the dispatcher's operation_id; otherwise the correlation handle promised in ImportDispatchResponse never reaches the caller.");
            await Assert.That(Guid.TryParse(operationId, out _)).IsTrue()
                .Because($"operation_id must be a parseable GUID (TimeUuid); got '{operationId}'.");
            await Assert.That(status).IsEqualTo("queued")
                .Because("A fresh dispatch against an empty per-system slot must surface as 'queued'; 'running' would indicate the controller is reporting the wrong claim outcome.");
        }
    }

    /// <summary>
    /// The PluralKit symmetric of the SP dispatch test. Pins that the
    /// <c>/api/settings/import-pk</c> endpoint also returns 202 Accepted with the same
    /// ImportDispatchResponse shape so the contract is uniform across platforms.
    /// </summary>
    [Test]
    public async Task ImportPk_FreshDispatch_Returns202WithOperationId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var principal = $"pk-import-202-{Guid.NewGuid():N}"[..24];
        await EnsureUserExistsAsync(client, principal);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/import-pk")
        {
            Content = JsonContent.Create(new { token = "synthetic-pk-token" }),
        };
        AttachPrincipalAuth(req, client, principal);

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Accepted)
                .Because($"PK dispatch must also return 202 — both platforms must share the same async-dispatch contract. Body: {body}");
            await Assert.That(ReadNestedString(body, "data", "status")).IsEqualTo("queued");
            await Assert.That(Guid.TryParse(ReadNestedString(body, "data", "operation_id"), out _)).IsTrue();
        }
    }

    /// <summary>
    /// The full dispatch -> worker -> bus chain: after the controller accepts the
    /// dispatch, the background worker must actually pick up the job and the lifecycle
    /// event must reach the cluster event bus. We assert on the failure event because
    /// the real SP runner has no live API to talk to in the test environment — but the
    /// shape of the assertion (Subscribe before POST, then await with timeout) is
    /// identical to the success-path one a future test could write with a stub runner.
    /// </summary>
    [Test]
    public async Task ImportSp_AfterDispatch_WorkerProcessesAndPublishesFailedEvent(CancellationToken token)
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var principal = $"sp-import-wf-{Guid.NewGuid():N}"[..24];
        await EnsureUserExistsAsync(client, principal);

        // Subscribe BEFORE dispatching so we can't miss the publish.
        using var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        subscribeCts.CancelAfter(TimeSpan.FromSeconds(15));
        await using var enumerator = fixture.Factory.EventBus
            .SubscribeAsync<SimplyPluralImportFailedEvent>(subscribeCts.Token)
            .GetAsyncEnumerator(subscribeCts.Token);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/import-sp")
        {
            Content = JsonContent.Create(new { token = "synthetic-sp-token" }),
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req, token);
        var body = await res.Content.ReadAsStringAsync(token);

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Accepted)
            .Because($"Setup invariant: this test only proves the worker runs after a successful dispatch. Body: {body}");

        // Drain the bus until we see a failed event for OUR principal (other parallel
        // tests may share the bus). The worker either gracefully fails or throws — both
        // routes publish SimplyPluralImportFailedEvent, which is the load-bearing
        // invariant: the client never gets stuck on Importing after a worker pickup.
        //
        // SimplyPluralImportFailedEvent.TargetSystemId is a ScopedSystemId whose .Value
        // is the scoped form ("nam:<raw>") produced by the JWT-derived principal path,
        // while `principal` above is the bare 24-char raw id this test minted. Comparing
        // .Value would ordinal-mismatch on the region prefix and drop every event on the
        // floor. Compare .RawId (the prefix-stripped form) instead so the shape matches
        // what `principal` holds.
        SimplyPluralImportFailedEvent? observed = null;
        while (await enumerator.MoveNextAsync())
        {
            if (string.Equals(enumerator.Current.TargetSystemId.RawId, principal, StringComparison.Ordinal))
            {
                observed = enumerator.Current;
                break;
            }
        }

        await Assert.That(observed).IsNotNull()
            .Because("The background worker must publish a SimplyPluralImportFailedEvent for our principal after the dispatch — otherwise the Compose app's Importing dialog would never resolve.");
    }

    /// <summary>
    /// Empty-token rejection: the handler must reject before claiming a slot, the
    /// controller must surface a non-202 error response. The exact error shape (4xx +
    /// JSON error body) is governed by the existing <c>InterfoldControllerBase</c>
    /// conflict mapping, but the load-bearing contract here is "no 2xx for empty input"
    /// so a runaway client can't burn LWT slots with garbage requests.
    /// </summary>
    [Test]
    public async Task ImportSp_EmptyToken_ReturnsErrorWithoutAccepting()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var principal = $"sp-import-bad-{Guid.NewGuid():N}"[..24];
        await EnsureUserExistsAsync(client, principal);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/import-sp")
        {
            Content = JsonContent.Create(new { token = "" }),
        };
        AttachPrincipalAuth(req, client, principal);

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsNotEqualTo(HttpStatusCode.Accepted)
                .Because($"Empty tokens must not be Accepted — otherwise the handler is silently producing valid dispatches for garbage input. Body: {body}");
            await Assert.That((int)res.StatusCode).IsGreaterThanOrEqualTo(400)
                .Because($"Empty token is invalid input and must surface as a 4xx error response, got {(int)res.StatusCode}. Body: {body}");
        }
    }
}
