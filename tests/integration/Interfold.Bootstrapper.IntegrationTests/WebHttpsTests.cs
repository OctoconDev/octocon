using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Bootstrapper.IntegrationTests.TestServices;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the opt-in <c>deployment.webHttps</c> path. When set, the bootstrapper
/// must emit a compose file where <c>octocon-web</c> bind-mounts the issued cert directory and
/// an nginx envsubst template, exposes :443, and uses the env vars that the template references.
/// The actual TLS handshake is asserted by spinning up just the web service inside DinD and
/// curling its own healthcheck endpoint over HTTPS via <c>docker exec</c>.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class WebHttpsTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PublishWithWebHttpsEmitsCertAndTemplateBindMounts()
    {
        // Pure publish-time contract: the bootstrapper should fill in the two new bind-mount
        // sources for octocon-web (cert dir + nginx envsubst template) and stamp the NGINX_*
        // env values that drive the template. No `docker compose up` here, so the test runs in
        // single-digit seconds against the shared DinD.
        var (scratch, _) = await dinD.PublishAsync(nameof(PublishWithWebHttpsEmitsCertAndTemplateBindMounts), TestConfigPaths.WebHttpsConfig);

        var envBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/.env");
        var env = DotEnvParser.ParseEnv(envBytes);

        // Every bind-mount-source line in the post-processed .env should resolve to an absolute
        // path. We don't assert the exact key names because Aspire derives them from the service
        // resource graph (currently `OCTOCON_WEB_BINDMOUNT_0`, `OCTOCON_WEB_BINDMOUNT_1`, ...);
        // we match the stable `OCTOCON_WEB_BINDMOUNT` prefix so a future Aspire upgrade that
        // tweaks the suffix shape (e.g. back to `OCTOCON_WEB_BINDMOUNTS__0`) still passes.
        var webBindMounts = env
            .Where(kv => kv.Key.StartsWith("OCTOCON_WEB_BINDMOUNT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Assert.That(webBindMounts.Count).IsGreaterThanOrEqualTo(2)
            .Because("expected at least two octocon-web bind mounts (certs + nginx template) " +
                     $"in the .env; got keys: {string.Join(", ", env.Keys)}");

        foreach (var (key, value) in webBindMounts)
        {
            await Assert.That(value).IsNotEmpty()
                .Because($"{key} should not be blank — PublishPhase failed to substitute the bind-mount source");
            await Assert.That(value).StartsWith("/")
                .Because($"{key}={value} must be an absolute host path");
        }

        // Spot-check the compose file itself: the cert mount target and the nginx template mount
        // target must both be present, and the service must publish container port 443 for HTTPS.
        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        await Assert.That(compose).Contains("octocon-web")
            .Because("octocon-web service must be present when deployment.webHttps=true");
        await Assert.That(compose).Contains("/certs")
            .Because("octocon-web must bind-mount the certs directory at /certs");
        await Assert.That(compose).Contains("/etc/nginx/templates/default.conf.template")
            .Because("octocon-web must bind-mount the nginx envsubst template");
        await Assert.That(compose).Contains("NGINX_SERVER_NAME")
            .Because("octocon-web must receive the NGINX_SERVER_NAME env var (consumed by the template)");
        await Assert.That(compose).Contains("NGINX_SSL_CERT_FILE")
            .Because("octocon-web must receive the NGINX_SSL_CERT_FILE env var (consumed by the template)");
        await Assert.That(compose).Contains("NGINX_SSL_KEY_FILE")
            .Because("octocon-web must receive the NGINX_SSL_KEY_FILE env var (consumed by the template)");
        await Assert.That(compose).Contains("NGINX_HTTPS_PORT_SUFFIX")
            .Because("octocon-web must receive the NGINX_HTTPS_PORT_SUFFIX env var so the 301 " +
                     "redirect from :80 points at the operator's webHttps host port, not the " +
                     "implicit :443 default");
        await Assert.That(compose).Contains($"\"{scratch.Ports.WebHttps}:443\"")
            .Because($"compose must publish the allocated webHttps host port ({scratch.Ports.WebHttps}) onto the container's :443 listener");

        // Negative invariant: the default (HTTP-only) variant of the web service emits a curl
        // healthcheck against the upstream image's :8080 listener. When webHttps=true we expect
        // the HTTPS variant of the healthcheck instead — the HTTP-only sentinel
        // "http://localhost:8080/" must not appear in the generated compose.
        await Assert.That(compose).DoesNotContain("http://localhost:8080/")
            .Because("the HTTP healthcheck variant must not be emitted when webHttps=true");
        await Assert.That(compose).Contains("https://localhost:443/")
            .Because("the HTTPS healthcheck must point at the in-container 443 listener");
    }

    [Test]
    public async Task WebContainerServesHttpsAfterComposeUp()
    {
        // End-to-end smoke: publish the compose, start ONLY the web container (no API / db
        // dependencies needed because the static site is self-contained), wait for nginx's own
        // healthcheck to flip green, then assert the rendered conf.d entry mentions the cert
        // paths we passed in. The healthcheck itself probes https://localhost:443/ with `-kf`
        // from inside the container, so a healthy state implies a successful TLS handshake
        // against the bootstrapper-issued leaf cert.
        var (scratch, _) = await dinD.PublishAsync(nameof(WebContainerServesHttpsAfterComposeUp), TestConfigPaths.WebHttpsConfig);

        // Bring up only the web service. --no-deps because the compose file networks reference
        // the api network but the web container itself has no service-level depends_on; compose
        // will create the network on demand. Pulling the upstream image happens here on first run.
        var up = await dinD.ExecAsync(
        [
            "sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml up -d --no-deps octocon-web 2>&1"
        ]);
        await Assert.That(up.ExitCode).IsEqualTo(0)
            .Because($"`docker compose up -d octocon-web` failed: {up.Stdout}{up.Stderr}");

        // Compose uses the parent directory name as the project name and renames containers
        // to `<project>-<service>-<n>`. Find the actual container id rather than guessing.
        var containerProbe = await dinD.ExecAsync(
        [
            "sh", "-c",
            $"docker compose -f {scratch.OutputDir}/docker-compose.yaml ps -q octocon-web"
        ]);
        var containerId = containerProbe.Stdout.Trim();
        await Assert.That(containerId).IsNotEmpty()
            .Because("expected `docker compose ps -q octocon-web` to print the running container id");

        // Poll the container's healthcheck (the compose file defines an HTTPS-aware curl probe
        // that exits 0 only when nginx finishes its TLS handshake). 60s budget covers cold pulls
        // of the upstream image on a fresh DinD plus the 10s start_period the healthcheck declares.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var lastHealth = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var inspect = await dinD.ExecAsync(
            [
                "sh", "-c",
                $"docker inspect --format '{{{{.State.Health.Status}}}}' {containerId}"
            ]);
            lastHealth = inspect.Stdout.Trim();
            if (string.Equals(lastHealth, "healthy", StringComparison.Ordinal)) break;
            if (string.Equals(lastHealth, "unhealthy", StringComparison.Ordinal))
            {
                // Surface the container logs immediately so the failure message is actionable.
                var logs = await dinD.ExecAsync(["docker", "logs", "--tail", "100", containerId]);
                throw new InvalidOperationException(
                    $"octocon-web reported unhealthy:\n{logs.Stdout}\n{logs.Stderr}");
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        await Assert.That(lastHealth).IsEqualTo("healthy")
            .Because($"octocon-web never became healthy (last status: '{lastHealth}'); see compose logs in artifacts");

        // Sanity check that envsubst actually rendered our template and substituted the
        // NGINX_* variables. We're not just trusting the healthcheck because the upstream image
        // could in theory ship its own /etc/nginx/conf.d/default.conf that masks the template.
        var renderedConf = await dinD.ExecAsync(
            ["docker", "exec", containerId, "cat", "/etc/nginx/conf.d/default.conf"]);
        await Assert.That(renderedConf.ExitCode).IsEqualTo(0)
            .Because($"could not read rendered nginx config: {renderedConf.Stderr}");
        await Assert.That(renderedConf.Stdout).Contains("/certs/leaf.crt")
            .Because("envsubst should have substituted NGINX_SSL_CERT_FILE into ssl_certificate");
        await Assert.That(renderedConf.Stdout).Contains("/certs/leaf.key")
            .Because("envsubst should have substituted NGINX_SSL_KEY_FILE into ssl_certificate_key");
        await Assert.That(renderedConf.Stdout).Contains("web.test.local")
            .Because("envsubst should have substituted NGINX_SERVER_NAME from the fixture's hosts[0]");

        // The 301 redirect must point at the operator's webHttps host port (the port the
        // bootstrapper mapped onto the container's :443 listener). Without the port suffix the
        // browser would resolve `https://$host/...` to port 443, which the bootstrapper does NOT
        // bind unless the operator picked 443 in interfold.bootstrap.json — sending the user to
        // a dead port.
        var expectedRedirect = scratch.Ports.WebHttps == 443
            ? "return 301 https://$host$request_uri;"
            : $"return 301 https://$host:{scratch.Ports.WebHttps}$request_uri;";
        await Assert.That(renderedConf.Stdout).Contains(expectedRedirect)
            .Because($"the rendered redirect must embed the operator's webHttps host port " +
                     $"({scratch.Ports.WebHttps}); got conf body:\n{renderedConf.Stdout}");

        // Live probe: hit the :80 listener and confirm nginx replies with a 301 whose Location
        // header references the operator-mapped HTTPS port (not nginx's implicit :443).
        var redirectProbe = await dinD.ExecAsync(
        [
            "docker", "exec", containerId,
            "curl", "-s", "-o", "/dev/null", "-D", "-", "http://localhost:80/"
        ]);
        await Assert.That(redirectProbe.ExitCode).IsEqualTo(0)
            .Because($"HTTP probe against octocon-web :80 failed: stdout='{redirectProbe.Stdout}' stderr='{redirectProbe.Stderr}'");
        var expectedLocationFragment = scratch.Ports.WebHttps == 443
            ? "Location: https://localhost/"
            : $"Location: https://localhost:{scratch.Ports.WebHttps}/";
        await Assert.That(redirectProbe.Stdout).Contains(expectedLocationFragment)
            .Because($"the 301 Location header must reference the operator-mapped webHttps port " +
                     $"({scratch.Ports.WebHttps}); got response headers:\n{redirectProbe.Stdout}");

        // Final TLS probe — `-k` because the leaf is signed by the bootstrapper's private root
        // CA which the curl invocation inside the container doesn't trust by default. Asserting
        // a 200 confirms the static asset path also wired up (the template's `try_files` SPA
        // fallback should serve index.html for `/`).
        var probe = await dinD.ExecAsync(
        [
            "docker", "exec", containerId,
            "curl", "-skf", "-o", "/dev/null", "-w", "%{http_code}", "https://localhost:443/"
        ]);
        await Assert.That(probe.ExitCode).IsEqualTo(0)
            .Because($"HTTPS probe against octocon-web failed: stdout='{probe.Stdout}' stderr='{probe.Stderr}'");
        await Assert.That(probe.Stdout.Trim()).IsEqualTo("200")
            .Because($"expected 200 from https://localhost:443/, got '{probe.Stdout.Trim()}'");
    }

    [Test]
    public async Task PublishWithWebHttpsLeavesEnvFilledWithAbsolutePaths()
    {
        // Companion to PublishWithWebHttpsEmitsCertAndTemplateBindMounts but focuses on the
        // operator-facing failure mode: a `KEY=` line with a blank RHS in the .env will cause
        // `docker compose up` to error out with "invalid mount source". This test confirms the
        // ApplyReplacementsToEnvFile pass actually populated every web-related entry.
        var (scratch, _) = await dinD.PublishAsync(nameof(PublishWithWebHttpsLeavesEnvFilledWithAbsolutePaths), TestConfigPaths.WebHttpsConfig);

        var envBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/.env");
        var rawText = Encoding.UTF8.GetString(envBytes);

        // No `OCTOCON_WEB_*=` line should land in the .env with a blank RHS. This guards against
        // a future Aspire upgrade that adds a new placeholder we forgot to teach
        // BuildEnvReplacements about.
        foreach (var line in rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (!trimmed.StartsWith("OCTOCON_WEB", StringComparison.OrdinalIgnoreCase)) continue;
            await Assert.That(trimmed.EndsWith('=')).IsFalse()
                .Because($"octocon-web env line has a blank value (compose up would fail): {trimmed}");
        }
    }
}



