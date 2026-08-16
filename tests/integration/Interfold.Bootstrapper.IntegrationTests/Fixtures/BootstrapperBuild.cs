using System.Diagnostics;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Session-wide caches of the expensive prep work used by every DinD fixture: the bootstrapper
/// publish output and the saved API image tarball. Both are produced once per test run.
/// </summary>
internal static class BootstrapperBuild
{
    /// <summary>
    /// Publishes the bootstrapper as a self-contained linux-x64 single-file binary and returns the
    /// containing directory. The directory also contains the support files we copy alongside in the
    /// real release tarball.
    /// </summary>
    public static readonly Lazy<Task<string>> PublishedDirectory = new(PublishBootstrapperAsync);

    /// <summary>
    /// Builds the API container image via Aspire's container publish, then docker-saves it to a
    /// tarball the DinD container can <c>docker load</c> into its inner daemon.
    /// </summary>
    public static readonly Lazy<Task<string>> ApiImageTarPath = new(BuildAndSaveApiImageAsync);

    private const string ApiImageRef = "interfold-api:test";

    // Root of the per-session publish + api-tar staging area under $TMPDIR. Every DinD
    // test-host process drops ~150 MB here (bootstrapper publish + saved API image tar)
    // and never cleans it up, so aborted / repeated sessions pile up on the runner disk.
    // PruneStaleSessions is called from both Lazy factories so the cleanup runs exactly
    // once per test-host regardless of which artifact is requested first.
    private static readonly string StagingRoot = Path.Combine(
        Path.GetTempPath(), "interfold-bootstrap-test-publish");
    private static int _prunedFlag;

    private static async Task<string> PublishBootstrapperAsync()
    {
        PruneStaleSessions();
        var outDir = Path.Combine(StagingRoot,
            $"run-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(outDir);

        await RunDotnetAsync(
            "publish",
            "tools/Interfold.Bootstrapper/Interfold.Bootstrapper.csproj",
            "/p:PublishProfile=linux-x64",
            "-o", outDir).ConfigureAwait(false);

        StageSupportFiles(outDir);

        return outDir;
    }

    private static async Task<string> BuildAndSaveApiImageAsync()
    {
        // Skip the `dotnet publish /t:PublishContainer` step if the local Docker daemon already
        // has a tagged `interfold-api:test` image. The PublishContainer task always re-fetches
        // the base layer manifest from mcr.microsoft.com, and that goes through whatever
        // credential helper Docker Desktop has configured. When the helper fails (no creds in
        // keychain, or MCR rate-limit + new auth-required tier) the whole test session aborts
        // before any DinD work runs. As long as we still have a fresh-enough cached build the
        // rebuild adds no signal — we just need *some* tarball to load into DinD.
        var alreadyExists = await DockerImageExistsAsync(ApiImageRef).ConfigureAwait(false);
        if (!alreadyExists)
        {
            await RunDotnetAsync(
                "publish",
                "hosts/Interfold.Api.Host/Interfold.Api.Host.csproj",
                "-c", "Release",
                "/t:PublishContainer",
                "/p:ContainerImageName=interfold-api",
                "/p:ContainerImageTag=test",
                "--os", "linux",
                "--arch", "x64").ConfigureAwait(false);
        }

        PruneStaleSessions();
        var tarPath = Path.Combine(StagingRoot, $"api-{Environment.ProcessId}.tar");
        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);

        await RunAsync("docker", new[] { "save", ApiImageRef, "-o", tarPath }, workingDir: RepoRoot.Path).ConfigureAwait(false);
        return tarPath;
    }

    // Removes `run-{pid}-{ts}/` dirs and `api-{pid}.tar` files whose PID is no longer
    // alive. Runs once per test-host process (`Interlocked.Exchange` gate) so the two
    // Lazy factories don't double-scan. Also schedules ProcessExit cleanup of the
    // CURRENT session's staging so a clean exit no longer leaks either — aborted
    // sessions still leak but the next successful run reclaims them.
    private static void PruneStaleSessions()
    {
        if (Interlocked.Exchange(ref _prunedFlag, 1) != 0) return;
        try
        {
            if (Directory.Exists(StagingRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(StagingRoot, "run-*"))
                {
                    if (IsForeignAlivePid(Path.GetFileName(dir), "run-")) continue;
                    TryDelete(() => Directory.Delete(dir, recursive: true));
                }
                foreach (var tar in Directory.EnumerateFiles(StagingRoot, "api-*.tar"))
                {
                    if (IsForeignAlivePid(Path.GetFileName(tar), "api-", ".tar")) continue;
                    TryDelete(() => File.Delete(tar));
                }
            }
        }
        catch
        {
            // Best-effort — a scan failure must not sink the whole test run.
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var thisPid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(StagingRoot, $"run-{thisPid}-*"))
                    TryDelete(() => Directory.Delete(dir, recursive: true));
                var tar = Path.Combine(StagingRoot, $"api-{thisPid}.tar");
                if (File.Exists(tar)) TryDelete(() => File.Delete(tar));
            }
            catch
            {
                // Best-effort — shutdown cleanup is not allowed to throw.
            }
        };
    }

    private static bool IsForeignAlivePid(string name, string prefix, string suffix = "")
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        var rest = name[prefix.Length..];
        if (suffix.Length > 0 && rest.EndsWith(suffix, StringComparison.Ordinal))
            rest = rest[..^suffix.Length];
        var dash = rest.IndexOf('-');
        var pidText = dash >= 0 ? rest[..dash] : rest;
        if (!int.TryParse(pidText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            return true;
        }
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void TryDelete(Action action)
    {
        try { action(); }
        catch { /* leftover on next run is fine; better than throwing on cleanup */ }
    }

    private static async Task<bool> DockerImageExistsAsync(string imageRef)
    {
        // `docker image inspect <ref>` exits 0 when the image is locally present, 1 otherwise.
        // We swallow exceptions because the goal is "do we have a usable cached image" - any
        // failure to interrogate the daemon just means we should fall back to the publish path.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                WorkingDirectory = RepoRoot.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("image");
            psi.ArgumentList.Add("inspect");
            psi.ArgumentList.Add(imageRef);

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void StageSupportFiles(string outDir)
    {
        // Mirror the release-tarball layout under the publish output. DatabaseInitPhase replaced
        // the old pg-bootstrap-auth / scylla-bootstrap-auth shell scripts, so the only support
        // files we still need next to the binary are:
        //   - scripts/docker/ensure-host-aio.sh : tunes fs.aio-max-nr for Scylla on DinD hosts
        //   - db/scylla/cassandra-rackdc.*.properties : bind-mounted into the scylla container
        Copy(RepoRoot.Combine("scripts", "docker", "ensure-host-aio.sh"),
             Path.Combine(outDir, "scripts", "docker", "ensure-host-aio.sh"));

        var rackDcDir = RepoRoot.Combine("db", "scylla");
        if (Directory.Exists(rackDcDir))
        {
            foreach (var src in Directory.EnumerateFiles(rackDcDir, "cassandra-rackdc.*.properties"))
            {
                var dest = Path.Combine(outDir, "db", "scylla", Path.GetFileName(src));
                Copy(src, dest);
            }
        }

        // Web TLS termination template. The bootstrapper bind-mounts this into octocon-web
        // when deployment.webHttps=true, so it must live next to the published binary even on
        // hosts where the operator doesn't use the web tier (publish itself is the same code path
        // either way — it's only the bind-mount entry in the .env that gets populated).
        var nginxTemplate = RepoRoot.Combine("web", "nginx", "default.conf.template");
        if (File.Exists(nginxTemplate))
        {
            Copy(nginxTemplate, Path.Combine(outDir, "web", "nginx", "default.conf.template"));
        }

        // Cassandra-only mode builds this Dockerfile at compose-up time (see CassandraImagePhase).
        var cassandraDockerfile = RepoRoot.Combine("db", "cassandra", "Dockerfile");
        if (File.Exists(cassandraDockerfile))
        {
            Copy(cassandraDockerfile, Path.Combine(outDir, "db", "cassandra", "Dockerfile"));
        }
    }

    private static void Copy(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: true);
    }

    private static Task RunDotnetAsync(params string[] args) =>
        RunAsync("dotnet", args, workingDir: RepoRoot.Path);

    private static async Task RunAsync(string fileName, IEnumerable<string> args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        var stdOutTask = proc.StandardOutput.ReadToEndAsync();
        var stdErrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args)} exited {proc.ExitCode}.\n" +
                $"stdout:\n{await stdOutTask.ConfigureAwait(false)}\n" +
                $"stderr:\n{await stdErrTask.ConfigureAwait(false)}");
        }
    }
}

