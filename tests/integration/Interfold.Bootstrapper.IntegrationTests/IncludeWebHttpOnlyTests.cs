using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the opt-in <c>deployment.includeWeb</c> path with TLS termination
/// turned off (<c>deployment.webHttps=false</c>). This is the HTTP-only debug variant — the
/// bootstrapper must:
/// <list type="bullet">
///   <item>Emit the <c>octocon-web</c> service in the generated compose so the wasm UI ships
///         alongside the API.</item>
///   <item>NOT bind-mount the leaf cert or nginx envsubst template — those are only useful for
///         the TLS-terminating variant covered by <see cref="WebHttpsTests"/>.</item>
///   <item>Map the operator's <c>ports.webHttp</c> / <c>ports.webHttps</c> entries onto the
///         upstream image's <c>:8080</c> listener (it ONLY listens there per its
///         <c>Dockerfile.wasm</c>) and emit an HTTP healthcheck against that port.</item>
/// </list>
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class IncludeWebHttpOnlyTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PublishWithIncludeWebHttpOnlyEmitsServiceWithoutTlsWiring()
    {
        // Pure publish-time contract: includeWeb=true && webHttps=false should land the
        // octocon-web service in the compose YAML, but WITHOUT the TLS-only bind mounts and env
        // vars. This is the "ship the wasm container for debugging behind an external proxy"
        // shape — the operator wants the container, doesn't want the bootstrapper to wire TLS
        // termination into it.
        var (scratch, _) = await dinD.PublishAsync(
            nameof(PublishWithIncludeWebHttpOnlyEmitsServiceWithoutTlsWiring), TestConfigPaths.WebHttpOnlyConfig);

        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        // The service must be present — that's the whole point of the flag.
        await Assert.That(compose).Contains("octocon-web")
            .Because("octocon-web service must be present when deployment.includeWeb=true even without webHttps");

        // The HTTP-only healthcheck path: the AppHost emits `curl http://localhost:8080/` for
        // the no-TLS variant because the upstream wasm image's nginx listens ONLY on :8080.
        await Assert.That(compose).Contains("http://localhost:8080/")
            .Because("HTTP-only octocon-web must healthcheck the upstream image's :8080 listener");

        // None of the TLS-only bits should appear — bind mounts, NGINX_* env vars, or the HTTPS
        // healthcheck variant. If any of these slip in the operator gets a half-configured
        // stack that either fails to start (mount source missing) or trips a TLS handshake
        // failure on the healthcheck.
        await Assert.That(compose).DoesNotContain("/etc/nginx/templates/default.conf.template")
            .Because("HTTP-only variant must NOT bind-mount the nginx envsubst template");
        await Assert.That(compose).DoesNotContain("NGINX_SERVER_NAME")
            .Because("HTTP-only variant must NOT emit NGINX_* env vars");
        await Assert.That(compose).DoesNotContain("NGINX_HTTPS_PORT_SUFFIX")
            .Because("HTTP-only variant has no redirect to stitch a port suffix into");
        await Assert.That(compose).DoesNotContain("https://localhost:443/")
            .Because("HTTP-only variant must NOT emit the HTTPS healthcheck variant");

        // Operator host port maps onto the upstream image's :8080 listener.
        await Assert.That(compose).Contains($"\"{scratch.Ports.WebHttp}:8080\"")
            .Because($"compose must publish the allocated webHttp host port ({scratch.Ports.WebHttp}) onto the upstream image's :8080");

        // The webHttps host port must NOT appear in the compose: with no TLS termination there
        // is no second listener inside the container to map it onto, and binding it anyway just
        // shadows webHttp on a second port and confuses operators who configured webHttps=false
        // expecting "no HTTPS at all". Match by `"<port>:` so we catch any target on the RHS.
        await Assert.That(compose).DoesNotContain($"\"{scratch.Ports.WebHttps}:")
            .Because($"HTTP-only variant must NOT bind webHttps host port ({scratch.Ports.WebHttps}); " +
                     "Ports:web-https is unused when webHttps=false");
    }
}



