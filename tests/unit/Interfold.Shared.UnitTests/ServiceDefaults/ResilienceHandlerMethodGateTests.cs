using System.Net;
using Interfold.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Interfold.Api.UnitTests.ServiceDefaults;

// Pins Extensions.ConfigureResilienceForGetOnly — the GET-only gate around the standard
// Microsoft.Extensions.Http.Resilience pipeline. Guards against the duplicate-SP-import
// class of bug where Polly's default POST retries multiplied state-changing work.
public sealed class ResilienceHandlerMethodGateTests
{
    [Test]
    public async Task GetReceiving503ThenOk_IsRetriedAndSucceeds()
    {
        var counter = new RequestCountingHandler(
            (request, attempt) => attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK));

        using var client = BuildHttpClient(counter);

        using var response = await client.GetAsync("https://example.test/probe", CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because("A GET with a transient 503 followed by a 200 should bubble up the eventual 200 — that's the point of retaining the standard pipeline for GETs.");
            await Assert.That(counter.AttemptCount).IsGreaterThanOrEqualTo(2)
                .Because("Polly must have made at least one retry attempt after the 503; otherwise the GET path has lost its safety net.");
        }
    }

    [Test]
    public async Task PostReceiving503_IsNotRetried()
    {
        var counter = new RequestCountingHandler(
            (request, attempt) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var client = BuildHttpClient(counter);

        using var response = await client.PostAsync(
            "https://example.test/mutation",
            new StringContent("{}"),
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable)
                .Because("The first 503 must surface to the caller unchanged — Polly retries on POST were the proximate cause of the duplicate-SP-import bug.");
            await Assert.That(counter.AttemptCount).IsEqualTo(1)
                .Because("A POST must be sent exactly once. Any retry here means the GET-only gate has regressed and we're back to the duplicate-state risk.");
        }
    }

    [Test]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    [Arguments("DELETE")]
    public async Task NonGetMutationVerbReceiving503_IsNotRetried(string methodName)
    {
        var counter = new RequestCountingHandler(
            (request, attempt) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var client = BuildHttpClient(counter);

        using var request = new HttpRequestMessage(new HttpMethod(methodName), "https://example.test/mutation");

        using var response = await client.SendAsync(request, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable)
                .Because($"{methodName} responses must not be retried — the GET-only gate covers every non-idempotent verb, not just POST.");
            await Assert.That(counter.AttemptCount).IsEqualTo(1)
                .Because($"{methodName} must be sent exactly once. Retries on this verb create the same duplicate-state risk POST does.");
        }
    }

    // SamplingDuration must be >= 2 * AttemptTimeout or the host fails to start.
    [Test]
    public async Task TimeoutOptions_AreConfiguredToTheDocumentedCeilings()
    {
        var options = new HttpStandardResilienceOptions();
        Extensions.ConfigureResilienceForGetOnly(options);

        using (Assert.Multiple())
        {
            await Assert.That(options.AttemptTimeout.Timeout).IsEqualTo(TimeSpan.FromSeconds(30))
                .Because("AttemptTimeout pins the per-attempt ceiling. Bumping it from the package default of 10 s is what made the SP import safe even without the async-job refactor.");
            await Assert.That(options.TotalRequestTimeout.Timeout).IsEqualTo(TimeSpan.FromMinutes(2))
                .Because("TotalRequestTimeout caps the whole pipeline including any GET retries. 2 min is the operational ceiling we agreed on.");
            await Assert.That(options.CircuitBreaker.SamplingDuration).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(60))
                .Because("Microsoft.Extensions.Http.Resilience validates SamplingDuration >= 2 * AttemptTimeout — with AttemptTimeout = 30 s this must be >= 60 s or the host fails to start.");
        }
    }

    private static HttpClient BuildHttpClient(HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("test")
            .AddStandardResilienceHandler().Configure(Extensions.ConfigureResilienceForGetOnly);

        // ConfigurePrimaryHttpMessageHandler is the factory hook for the innermost handler
        // — replacing the primary handler after the resilience handler wraps the test transport.
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        return factory.CreateClient("test");
    }

    private sealed class RequestCountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responseFactory;
        private int _attempts;

        public RequestCountingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int AttemptCount => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            var response = _responseFactory(request, attempt);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
