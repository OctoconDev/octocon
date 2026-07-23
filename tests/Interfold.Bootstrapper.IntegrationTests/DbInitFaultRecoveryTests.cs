using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Shared.Contracts.Configuration;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Confirms the bootstrapper recovers cleanly from a fault injected between the Postgres and
/// Scylla initialisation sub-steps inside <c>DatabaseInitPhase</c>. This exercises the
/// idempotent state probes (<c>PostgresAlreadyInitializedAsync</c> /
/// <c>ScyllaAlreadyInitializedAsync</c>) that decide whether a rerun should reapply admin
/// bootstrap or short-circuit.
/// </summary>
/// <remarks>
/// The first run uses the hidden <c>--fault-inject=after-db-postgres</c> flag (wired into
/// <see cref="Interfold.Bootstrapper.Phases.DatabaseInitPhase"/>) which throws inside the phase
/// right after the Postgres roles are minted. The Orchestrator catches the throw, returns
/// non-zero, and skips Launch — leaving the postgres data volume populated with the
/// <c>interfold_admin</c> role but no scylla role yet.
///
/// Runs against its own per-test host-port window inside the shared DinD (via
/// <see cref="DinDFixtureBase.CreateScratchAsync"/>'s port allocator), so it can execute
/// alongside any other compose-up test without colliding on postgres/scylla/api ports.
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class DbInitFaultRecoveryTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task PartiallyBootstrappedDbCompletesOnRerun()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(PartiallyBootstrappedDbCompletesOnRerun), TestConfigPaths.DefaultConfig);

        // Run #1: bootstrap halts mid-DatabaseInitPhase, after Postgres admin role creation but
        // before Scylla. Exit code is non-zero because the phase throws to skip Launch (we don't
        // want compose up against an un-initialised scylla).
        var halted = await dinD.RunOnScratchAsync(scratch, $"{nameof(PartiallyBootstrappedDbCompletesOnRerun)}-halt", "bootstrap",
            "--skip-prereqs", "--fault-inject=after-db-postgres");
        await Assert.That(halted.ExitCode).IsNotEqualTo(0)
            .Because("fault-inject=after-db-postgres should surface a non-zero exit so launch is skipped");

        // The postgres compose service must be running and the admin role must exist.
        var composeFile = $"{scratch.OutputDir}/docker-compose.yaml";
        var pgAdminProbe = await dinD.PsqlAsync(
            composeFile, PostgresRoles.Init, "postgres",
            "\"SELECT rolsuper FROM pg_roles WHERE rolname='interfold_admin'\"",
            host: null);
        await Assert.That(pgAdminProbe.ExitCode).IsEqualTo(0L)
            .Because($"postgres should be running after a partial bootstrap: {pgAdminProbe.Stderr}");
        await Assert.That(pgAdminProbe.Stdout.Trim()).IsEqualTo("t")
            .Because("interfold_admin postgres role must have been created during the halted run");

        // The scylla side should NOT have an admin role yet — that's the bit the fault skipped.
        // (We don't assert scylla container *isn't* running — the postgres init sub-phase brings
        // both stateful services up via `docker compose up -d msg-db scylla`.)
        //
        // Using `LIST ROLES` (the full listing) rather than `LIST ROLES OF '<role>'` because the
        // latter errors with `<role 'interfold_admin'> doesn't exist` on a missing role — the error
        // text contains the role name we're checking for, producing a false-positive match. The
        // full listing only mentions a role if it actually exists in the cluster.
        var scyllaAdminProbe = await dinD.CqlshAsync(composeFile, "cassandra", "cassandra", "\"LIST ROLES\"");
        await Assert.That(scyllaAdminProbe.Stdout.Contains("interfold_admin")).IsFalse()
            .Because("the scylla admin role must NOT exist yet after a halt-after-postgres run");

        // Run #2: rerun without the fault flag. The orchestrator hits db-init again, the
        // postgres state probe short-circuits the admin bootstrap, scylla init runs fresh, and
        // launch brings everything up to health.
        var resumed = await dinD.RunOnScratchAsync(scratch, $"{nameof(PartiallyBootstrappedDbCompletesOnRerun)}-resume", "bootstrap",
            "--skip-prereqs", "--print-phase-status");
        await Assert.That(resumed.ExitCode).IsEqualTo(0)
            .Because($"resumed bootstrap should complete cleanly: {resumed.Stderr}");
        await Assert.That(resumed.Stdout)
            .Contains("postgres already initialised")
            .Or.Contains("postgres state probe")
            .Because("the postgres state probe should surface the short-circuit on rerun");

        // Final state: scylla admin must now exist, locking the original cassandra default.
        // Resolve the password C#-side and pass it through CqlshAsync's `password` parameter —
        // the same shape sibling tests in DbInitSecurityInvariantsTests use, and immune to
        // the JSON-writer drift the shell `grep | sed` variant would silently break under.
        var scyllaAdminPassword = await dinD.ReadSecretsFieldAsync(scratch, "scyllaAdminPassword");
        var scyllaAdminFinal = await dinD.CqlshAsync(
            composeFile, "interfold_admin", scyllaAdminPassword, "\"LIST ROLES\"");
        await Assert.That(scyllaAdminFinal.Stdout.Contains("interfold_admin")).IsTrue()
            .Because($"after resume, interfold_admin must exist in scylla: {scyllaAdminFinal.Stdout}");
    }
}



