using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Argv-shape assertions for <see cref="RestorePhase"/>. Pins the pg_restore + docker cp
/// command layout so a refactor that swaps flag order or drops `--clean` catches at
/// unit speed rather than only in the DinD integration suite.
/// </summary>
public sealed class RestoreCommandBuildingTests
{
    [Test]
    public async Task PgRestoreArgsIncludeCleanIfExistsAndSingleTransaction()
    {
        var args = RestorePhase.BuildPgRestoreArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            adminUser: "interfold_admin",
            database: "interfold");

        // --clean --if-exists makes the restore idempotent (DROP ... IF EXISTS statements
        // before the recreate). --single-transaction gives all-or-nothing semantics so a
        // partial-failure re-run doesn't leave a half-restored soup. --no-owner strips the
        // owner clauses since the destination role may not match the source dump.
        await Assert.That(args).IsEquivalentTo(new[]
        {
            "compose", "-f", "/srv/deploy/docker-compose.yaml",
            "exec", "-T",
            "--env", Interfold.Bootstrapper.Util.DatabaseArchiveStreamer.PgPasswordEnvVar,
            "msg-db",
            "pg_restore",
            "-U", "interfold_admin",
            "-d", "interfold",
            "--clean", "--if-exists",
            "--single-transaction",
            "--no-owner",
        });
    }

    [Test]
    public async Task PgRestoreArgsDoNotContainPlaintextPassword()
    {
        // Same contract as pg_dump: password reaches the process via PGPASSWORD env var,
        // never argv. `ps` output must not leak the admin credential.
        var args = RestorePhase.BuildPgRestoreArgs(
            composeFile: "/srv/deploy/docker-compose.yaml",
            adminUser: "interfold_admin",
            database: "interfold");

        foreach (var arg in args)
        {
            await Assert.That(arg).DoesNotContain("password");
            await Assert.That(arg).DoesNotContain("secret");
        }
    }

    [Test]
    public async Task BuildContainerCpWriteArgsStreamsStdinToContainer()
    {
        // `docker cp - <id>:<path>` is the inverse of the backup path — reads a raw tar
        // from stdin and extracts it into the container's <path>. Order matters: `-`
        // must precede the container:path spec, not the other way around.
        var args = RestorePhase.BuildContainerCpWriteArgs(
            containerId: "63fe606f0e95",
            dataPath: ContainerMountPaths.ScyllaData);

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "cp", "-", $"63fe606f0e95:{ContainerMountPaths.ScyllaData}",
        });
    }

    [Test]
    public async Task BuildContainerCpWriteArgsRejectsBlankInputs()
    {
        Assert.Throws<ArgumentException>(
            () => RestorePhase.BuildContainerCpWriteArgs(containerId: "", dataPath: ContainerMountPaths.ScyllaData));
        Assert.Throws<ArgumentException>(
            () => RestorePhase.BuildContainerCpWriteArgs(containerId: "abc", dataPath: "   "));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolveLatestArchivePicksNewestByMtime()
    {
        // Test-only file staging: three files with distinct mtimes; ResolveLatestArchive
        // must pick the one with the newest timestamp regardless of alphabetical order.
        using var scratch = TestSupport.NewScratchDir("interfold-restore-latest");
        var tmpDir = scratch.Path;
        var oldest = Path.Combine(tmpDir, "20260101-000000.dump");
        var middle = Path.Combine(tmpDir, "20260201-000000.dump");
        var newest = Path.Combine(tmpDir, "20260301-000000.dump");
        await File.WriteAllTextAsync(oldest, "");
        await File.WriteAllTextAsync(middle, "");
        await File.WriteAllTextAsync(newest, "");
        File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(middle, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var picked = BackupStoragePaths.LatestFile(tmpDir, "*.dump");

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.FullName).IsEqualTo(newest);
    }

    [Test]
    public async Task ResolveLatestArchiveReturnsNullOnMissingDirectory()
    {
        // Fresh installs will not have a {backupRoot}/scylla/ before the first backup.
        // The resolver must return null in that case, not throw — the caller checks for
        // null and surfaces "no archive found" in the operator-facing error.
        var picked = BackupStoragePaths.LatestFile(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")),
            "*.dump");

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task ResolveLatestArchiveReturnsNullWhenPatternDoesNotMatch()
    {
        // Directory exists but contains only non-matching files.
        using var scratch = TestSupport.NewScratchDir("interfold-restore-nomatch");
        var tmpDir = scratch.Path;
        await File.WriteAllTextAsync(Path.Combine(tmpDir, "readme.txt"), "");

        var picked = BackupStoragePaths.LatestFile(tmpDir, "*.dump");

        await Assert.That(picked).IsNull();
    }
}
