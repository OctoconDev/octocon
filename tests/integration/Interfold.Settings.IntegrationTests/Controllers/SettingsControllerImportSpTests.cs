using System.Net;
using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Contracts.Models.Read;
using Interfold.Settings.Contracts.Models.Wire;

namespace Interfold.Settings.IntegrationTests.Controllers;

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
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("sp-import-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import-sp",
            new SettingsImportRequest(new ImportToken("synthetic-sp-token")),
            principal);
        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.OperationId.Value)
                .IsNotEqualTo(Guid.Empty)
                .Because("The response body must carry the dispatcher's operation_id; otherwise the correlation handle promised in ImportDispatchResponse never reaches the caller.");
            await Assert.That(envelope.Data.Status)
                .IsEqualTo(ImportOperationDispatchStatus.Queued)
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
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("pk-import-202", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import-pk",
            new SettingsImportRequest(new ImportToken("synthetic-pk-token")),
            principal);
        var envelope = await res.ReadEnvelopeAsync<ImportDispatchResponse>(HttpStatusCode.Accepted);

        using (Assert.Multiple())
        {
            await Assert.That(envelope.Data.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(envelope.Data.OperationId.Value).IsNotEqualTo(Guid.Empty);
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
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("sp-import-wf", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        // Subscribe BEFORE dispatching so we can't miss the publish.
        using var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        subscribeCts.CancelAfter(TimeSpan.FromSeconds(15));
        await using var enumerator = fixture.Factory.EventBus
            .SubscribeAsync<SimplyPluralImportFailedEvent>(subscribeCts.Token)
            .GetAsyncEnumerator(subscribeCts.Token);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import-sp",
            new SettingsImportRequest(new ImportToken("synthetic-sp-token")),
            principal,
            ct: token);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Accepted)
            .Because($"Setup invariant: this test only proves the worker runs after a successful dispatch. Body: {await res.Content.ReadAsStringAsync(token)}");

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
    /// controller must surface a non-202 error response. The <see cref="ImportToken"/>
    /// wrapper accepts empty strings at construction (the whole point of the test is
    /// to see the handler reject them), so we build the record directly with an empty
    /// token rather than the raw JSON body the pre-sweep test used.
    /// </summary>
    [Test]
    public async Task ImportSp_EmptyToken_ReturnsErrorWithoutAccepting()
    {
        using var client = TestClient.NoRedirect(fixture);
        var principal = TestIds.NewSystemId("sp-import-bad", maxLen: 24);
        await EnsureUserExistsAsync(client, principal);

        using var res = await client.SendAsJsonAsync(
            HttpMethod.Post, "/api/settings/import-sp",
            new SettingsImportRequest(new ImportToken("")),
            principal);

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsNotEqualTo(HttpStatusCode.Accepted)
                .Because($"Empty tokens must not be Accepted — otherwise the handler is silently producing valid dispatches for garbage input. Body: {await res.Content.ReadAsStringAsync()}");
            await Assert.That((int)res.StatusCode).IsGreaterThanOrEqualTo(400)
                .Because($"Empty token is invalid input and must surface as a 4xx error response, got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
        }
    }
}
