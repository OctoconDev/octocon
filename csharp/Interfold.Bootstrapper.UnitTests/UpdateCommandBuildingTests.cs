using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Contracts.Enums;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Argv-shape assertions for <see cref="UpdateImagesPhase"/>. Mirrors the pattern in
/// <see cref="BackupCommandBuildingTests"/> — pins the docker compose command layout so
/// a future refactor that swaps flag order or drops a service filter catches the
/// change at unit speed instead of only in DinD integration tests.
/// </summary>
public sealed class UpdateCommandBuildingTests
{
    [Test]
    public async Task ComposePullWithoutServicesTargetsEveryService()
    {
        // Empty services list is compose's "act on every service" idiom. The argv must
        // end with `pull` and nothing after it — appending a stray "" would make
        // compose interpret it as an empty-name service and fail.
        var args = UpdateImagesPhase.BuildComposePullArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            services: []);

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml", "pull",
        });
    }

    [Test]
    public async Task ComposePullWithServicesAppendsServiceList()
    {
        var args = UpdateImagesPhase.BuildComposePullArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            services: ["interfold-api", "octocon-web"]);

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml", "pull",
            "interfold-api", "octocon-web",
        });
    }

    [Test]
    public async Task ComposeUpWithoutServicesActsOnEveryService()
    {
        var args = UpdateImagesPhase.BuildComposeUpArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            services: []);

        // `-d` (detach) is load-bearing — an interactive `up` would block the update
        // workflow forever.
        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml", "up", "-d",
        });
    }

    [Test]
    public async Task ComposeUpWithServicesNarrowsRecreate()
    {
        var args = UpdateImagesPhase.BuildComposeUpArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            services: ["interfold-api"]);

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml", "up", "-d",
            "interfold-api",
        });
    }

    [Test]
    public async Task ComposeImagesRequestsJsonFormat()
    {
        // The digest diff depends on `--format json` — the default text output isn't
        // parseable enough to reliably extract per-service image IDs.
        var args = UpdateImagesPhase.BuildComposeImagesArgs(
            composeFile: "/srv/deploy/docker-compose.yaml");

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml", "images", "--format", "json",
        });
    }

    [Test]
    public async Task ComposeLogsClampsTailToOperatorValue()
    {
        // The failure-diagnosis path dumps the last N lines of a failing container's
        // logs. N is the caller's choice — we default to 200 in UpdateImagesPhase but
        // the argv builder itself must respect whatever's handed in.
        var args = UpdateImagesPhase.BuildComposeLogsArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            service: "interfold-api",
            tail: 50);

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "logs", "--tail", "50", "interfold-api",
        });
    }

    [Test]
    public async Task ResolveServiceWhitelistCliBeatsConfig()
    {
        // CLI --service wins over config.update.services. The operator's ad-hoc override
        // is always the more specific intent.
        var options = MakeOptions(updateServices: ["interfold-api"]);
        var config = new BootstrapConfig { Update = { Services = ["msg-db", "scylla"] } };

        var resolved = UpdateImagesPhase.ResolveServiceWhitelist(options, config);

        await Assert.That(resolved).IsEquivalentTo(new[] { "interfold-api" });
    }

    [Test]
    public async Task ResolveServiceWhitelistFallsBackToConfig()
    {
        // No CLI → use the persistent config value.
        var options = MakeOptions(updateServices: null);
        var config = new BootstrapConfig { Update = { Services = ["msg-db"] } };

        var resolved = UpdateImagesPhase.ResolveServiceWhitelist(options, config);

        await Assert.That(resolved).IsEquivalentTo(new[] { "msg-db" });
    }

    [Test]
    public async Task ResolveServiceWhitelistBothEmptyMeansEveryService()
    {
        // The empty sentinel propagates through — UpdateImagesPhase interprets an empty
        // result as "pass no service names to docker compose", which compose reads as
        // "every service in the file".
        var options = MakeOptions(updateServices: null);
        var config = new BootstrapConfig();

        var resolved = UpdateImagesPhase.ResolveServiceWhitelist(options, config);

        await Assert.That(resolved.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DiffDigestsReturnsChangedServicesOnly()
    {
        // Only the services whose image ID changed appear in the diff. Unchanged
        // services stay off the recreate list, which is the whole point of the diff.
        var before = new Dictionary<string, string>
        {
            ["msg-db"] = "sha256:aaa",
            ["scylla"] = "sha256:bbb",
            ["interfold-api"] = "sha256:ccc",
        };
        var after = new Dictionary<string, string>
        {
            ["msg-db"] = "sha256:aaa",
            ["scylla"] = "sha256:bbb",
            ["interfold-api"] = "sha256:ddd",
        };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed).IsEquivalentTo(new[] { "interfold-api" });
    }

    [Test]
    public async Task DiffDigestsHandlesNewService()
    {
        var before = new Dictionary<string, string> { ["msg-db"] = "sha256:aaa" };
        var after = new Dictionary<string, string>
        {
            ["msg-db"] = "sha256:aaa",
            ["new-svc"] = "sha256:new",
        };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed).Contains("new-svc");
    }

    [Test]
    public async Task DiffDigestsHandlesRemovedService()
    {
        var before = new Dictionary<string, string>
        {
            ["msg-db"] = "sha256:aaa",
            ["dropped"] = "sha256:x",
        };
        var after = new Dictionary<string, string> { ["msg-db"] = "sha256:aaa" };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed).Contains("dropped");
    }

    [Test]
    public async Task DiffDigestsAllSameReturnsEmpty()
    {
        // The "no-op" case: every image ID matches. UpdateImagesPhase short-circuits
        // here and skips the recreate + health check + downtime.
        var before = new Dictionary<string, string>
        {
            ["msg-db"] = "sha256:aaa",
            ["scylla"] = "sha256:bbb",
        };
        var after = new Dictionary<string, string>
        {
            ["msg-db"] = "sha256:aaa",
            ["scylla"] = "sha256:bbb",
        };

        var changed = UpdateImagesPhase.DiffDigests(before, after);
        await Assert.That(changed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ShouldRebuildCassandraFalseInScyllaMode()
    {
        // databaseMode=single (Scylla) never triggers a Cassandra rebuild — the local
        // interfold-cassandra:local image isn't part of a Scylla-only stack, and any
        // whitelist value must be ignored here. Pins the mode gate.
        var config = new BootstrapConfig { DatabaseMode = DatabaseMode.Single };

        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, [])).IsFalse();
        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, ["cassandra"])).IsFalse();
        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, ["msg-db", "cassandra"])).IsFalse();
    }

    [Test]
    public async Task ShouldRebuildCassandraTrueInCassandraModeWithEmptyWhitelist()
    {
        // Empty whitelist = "act on every service" (compose semantics propagated by
        // ResolveServiceWhitelist), so cassandra is implicitly in scope and must rebuild.
        var config = new BootstrapConfig { DatabaseMode = DatabaseMode.Cassandra };

        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, [])).IsTrue();
    }

    [Test]
    public async Task ShouldRebuildCassandraTrueWhenWhitelistNamesCassandra()
    {
        // Explicit ["cassandra"] whitelist means "just rebuild the DB image" — this is the
        // deliberate "I patched the Dockerfile, only re-cook the Cassandra layer" path.
        var config = new BootstrapConfig { DatabaseMode = DatabaseMode.Cassandra };

        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, ["cassandra"])).IsTrue();
        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, ["cassandra", "interfold-api"])).IsTrue();
    }

    [Test]
    public async Task ShouldRebuildCassandraFalseWhenWhitelistExcludesCassandra()
    {
        // The whole point of the scoped rebuild: an operator narrowing an update with
        // `--service msg-db` must NOT get an unrelated Cassandra rebuild that would
        // pay the docker-build cost (and potentially reset the running container's
        // Dockerfile-baked customisations) for no reason.
        var config = new BootstrapConfig { DatabaseMode = DatabaseMode.Cassandra };

        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, ["msg-db"])).IsFalse();
        await Assert.That(UpdateImagesPhase.ShouldRebuildCassandra(config, ["interfold-api", "octocon-web"])).IsFalse();
    }

    private static BootstrapOptions MakeOptions(string[]? updateServices)
    {
        return new BootstrapOptions(
            Command: BootstrapCommand.UpdateImages,
            ConfigPath: null,
            OutputDir: Path.GetFullPath("./deploy"),
            SkipPrereqs: false,
            RotateSecrets: false,
            RotateCerts: false,
            NonInteractive: false,
            FaultInject: null,
            PrintPhaseStatus: false,
            UpdateServices: updateServices);
    }
}
