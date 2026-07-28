using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection; // AssemblyMetadataAttribute; System.Reflection.Assembly is qualified inline (collides with TUnit.HookType.Assembly).
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.IntegrationTests.Shared.TestServices.TestBench;

/// <summary>Auto-managed centralised DB bench for integration tests. First test-host
/// attaches with a cold state → launches the AppHost as a detached child process in
/// <see cref="AppHostParameterKeys.TestBenchMode"/>, waits for the readiness marker, then
/// lets the AppHost exit (persistent-lifetime containers survive it). Subsequent attaches
/// see hot ports and skip the AppHost entirely.</summary>
/// <remarks>
/// <para>
/// Opt-in via <see cref="OptInEnvVar"/> — a null / empty / <c>"0"</c> value means the caller
/// falls back to the legacy per-project <c>AspireFixture</c> path. That's the CI default and
/// the escape hatch for anyone who wants the old behaviour locally.
/// </para>
/// <para>
/// Cross-process coordination: all state is under <see cref="TestBenchLease.GetLeaseDirectory"/>,
/// mutations are gated on <see cref="TestBenchLease.AcquireAsync"/>. The reaper runs
/// under the same lock at the top of <see cref="EnsureRunningAsync"/> so exactly one
/// process decides teardown.
/// </para>
/// </remarks>
public static class TestBenchCoordinator
{
    /// <summary>Env-var override for the opt-in decision. Highest precedence; use <c>"0"</c>
    /// or <c>"false"</c> to force the legacy path (CI leg) and <c>"1"</c> or <c>"true"</c>
    /// to force bench mode regardless of the compile-time default.</summary>
    public const string OptInEnvVar = "INTERFOLD_TEST_BENCH";

    /// <summary>Assembly-metadata key the integration test csproj sets to <c>"1"</c> via
    /// <c>tests/Directory.Build.props</c>. It's the only reliable channel we have because
    /// <see cref="OptInEnvVar"/> supplied via a runsettings <c>&lt;EnvironmentVariables&gt;</c>
    /// block never reaches the test-host under MTP + TUnit — MTP's <c>dotnet test</c>
    /// integration doesn't include the vstest bridge that would honour that XML. Baking
    /// the flag into the assembly at build time keeps <c>dotnet test</c>, direct DLL
    /// launches, and IDE runners all in lockstep.</summary>
    private const string OptInAssemblyMetadataKey = "InterfoldTestBenchOptIn";

    /// <summary>Machine-readable marker <c>TestBenchReadyEmitter</c> writes to stdout once
    /// every DB resource is Running. Keep in sync with the AppHost side.</summary>
    private const string ReadyLinePrefix = "[test-bench] ready";

    /// <summary>Fixed host ports — the bench uses one set forever so cross-process attach
    /// can trust the numbers without discovery.</summary>
    private static readonly BenchPorts DefaultPorts = new();

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ApplyOnceLockTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LauncherTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Returns true when the current test host is opted in. Env-var override wins,
    /// otherwise the compile-time <see cref="OptInAssemblyMetadataKey"/> attribute on the
    /// entry assembly decides. Consumers use this to attach to the bench vs. legacy path.</summary>
    public static bool IsOptedIn()
    {
        if (TryReadBoolWire(Environment.GetEnvironmentVariable(OptInEnvVar)) is bool envDecision)
            return envDecision;
        return TryReadBoolWire(GetAssemblyOptInValue()) ?? false;
    }

    private static string? GetAssemblyOptInValue()
    {
        // Read the opt-in flag ONLY from the test-host's entry assembly (e.g.
        // Interfold.Auth.IntegrationTests.dll). tests/Directory.Build.targets injects the
        // metadata at the leaf csproj level, honouring the per-project
        // <InterfoldTestBenchOptOut> override — a leaf that opts out (Infrastructure, for
        // MigrationLedgerTests) simply has NO metadata baked in and IsOptedIn() therefore
        // falls through to false.
        //
        // Do NOT fall back to typeof(TestBenchCoordinator).Assembly — Interfold.IntegrationTests.Shared
        // also lives under tests/integration/ and therefore also carries the opt-in metadata,
        // which would silently override every leaf's opt-out. The July 2026 concurrent run
        // showed exactly that failure mode: Infrastructure ran against the shared bench,
        // peer projects' migrations wrote extra ledger rows, and
        // Scylla_RerunningMigrations_IsANoOp saw 14 → 16 between snapshots.
        return FindMetadataValue(System.Reflection.Assembly.GetEntryAssembly());
    }

    private static string? FindMetadataValue(System.Reflection.Assembly? assembly)
    {
        if (assembly is null) return null;
        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, OptInAssemblyMetadataKey, StringComparison.Ordinal))
                return attr.Value;
        }
        return null;
    }

    private static bool? TryReadBoolWire(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (string.Equals(raw, "0", StringComparison.Ordinal)
            || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return null;
    }

    /// <summary>Ensures the bench containers are running and returns connection info. May
    /// spawn a detached AppHost process on cold-boot. Cross-process safe.</summary>
    public static async Task<BenchConnectionInfo> EnsureRunningAsync(CancellationToken ct)
    {
        using var _ = await TestBenchLease.AcquireAsync(LockTimeout, ct).ConfigureAwait(false);

        var state = TestBenchLease.ReadState();
        state.Users = TestBenchLease.PruneStale(state.Users);

        // Reaper first: if the bench has aged out with nobody attached, tear it down
        // and continue as if we were cold-booting.
        if (await TestBenchIdleReaper.RunIfIdleAsync(state, ct).ConfigureAwait(false))
        {
            state = new TestBenchLeaseState();
        }

        var ports = state.Ports ?? DefaultPorts;

        var hot = await ArePortsHotAsync(ports, ct).ConfigureAwait(false);
        if (!hot)
        {
            Console.WriteLine("[test-bench] cold ports; launching AppHost to boot containers");
            await LaunchAppHostAsync(ports, ct).ConfigureAwait(false);
            // Fresh containers → any recorded migration state is stale. Clearing here
            // means the first project to attach will re-run migrations against the new
            // volumes; all subsequent attaches skip via `TryClaimMigrationAsync` below.
            state.MigrationsApplied.Clear();
        }

        state.Ports = ports;
        state.LastAttachUtc = DateTime.UtcNow;
        state.Users.Add(TestBenchLease.CurrentUser());
        TestBenchLease.WriteState(state);

        return BuildConnectionInfo(ports);
    }

    /// <summary>Cross-process one-shot for idempotent bench-setup work (DB migrations,
    /// keyspace seeds, etc.). The delegate is invoked exactly once per bench-lifetime;
    /// concurrent callers block on the file lock, then observe the completed marker and
    /// return without running the delegate.</summary>
    /// <remarks>
    /// The whole delegate runs while the file lock is held so concurrent
    /// <see cref="MigrationLedgerTests"/> in <c>Interfold.Infrastructure.IntegrationTests</c>
    /// can't race a peer project's <see cref="SharedDbFixture"/> initial migration pass —
    /// see <c>docs/test-bench-audit.md</c>. Bump <see cref="ApplyOnceLockTimeout"/> if a
    /// migration ever grows past a few minutes.
    /// </remarks>
    public static async Task ApplyOnceAsync(string key, Func<Task> work, CancellationToken ct)
    {
        if (!IsOptedIn())
        {
            await work().ConfigureAwait(false);
            return;
        }

        using var _ = await TestBenchLease.AcquireAsync(ApplyOnceLockTimeout, ct).ConfigureAwait(false);
        var state = TestBenchLease.ReadState();
        if (state.MigrationsApplied.TryGetValue(key, out var applied) && applied)
            return;

        await work().ConfigureAwait(false);

        state.MigrationsApplied[key] = true;
        state.LastAttachUtc = DateTime.UtcNow;
        TestBenchLease.WriteState(state);
    }

    /// <summary>Removes the current process from the lease's users list. Never throws;
    /// bench teardown happens asynchronously via the reaper on the next attach.</summary>
    public static async Task DetachAsync(CancellationToken ct)
    {
        try
        {
            using var _ = await TestBenchLease.AcquireAsync(LockTimeout, ct).ConfigureAwait(false);
            var state = TestBenchLease.ReadState();
            var currentPid = Environment.ProcessId;
            state.Users = state.Users
                .Where(u => u.Pid != currentPid)
                .ToList();
            state.Users = TestBenchLease.PruneStale(state.Users);
            state.LastAttachUtc = DateTime.UtcNow;
            TestBenchLease.WriteState(state);
        }
        catch
        {
            // Best-effort — never fail teardown because of a stuck lock or filesystem hiccup.
        }
    }

    /// <summary>Convenience: returns the postgres init connection string with app credentials
    /// swapped in for a caller that already knows the bench is up.</summary>
    public static BenchConnectionInfo BuildConnectionInfo(BenchPorts ports)
    {
        var initCs =
            $"Host=127.0.0.1;Port={ports.Postgres};" +
            $"Username={DbInitHelper.PostgresInitUser};Password={TestDbCredentials.PostgresInitPassword};" +
            "Database=postgres;SSL Mode=Disable";
        var appCs =
            $"Host=127.0.0.1;Port={ports.Postgres};" +
            $"Username={TestDbCredentials.PostgresAppUser};Password={TestDbCredentials.PostgresAppPassword};" +
            $"Database={DbInitHelper.DefaultPostgresDb};SSL Mode=Disable;Maximum Pool Size=5";
        return new BenchConnectionInfo(
            PostgresConnectionStringForInit: initCs,
            PostgresConnectionStringForApp: appCs,
            ScyllaPort: ports.Scylla,
            CassandraPort: ports.Cassandra,
            LeaseFilePath: TestBenchLease.GetStateFilePath());
    }

    /// <summary>Probes all three fixed ports with a bounded timeout. All three must respond
    /// or we take the cold path — a half-up bench needs the launcher to bring the missing
    /// engine back up.</summary>
    private static async Task<bool> ArePortsHotAsync(BenchPorts ports, CancellationToken ct)
    {
        return await ProbeAsync(ports.Postgres, ct).ConfigureAwait(false)
            && await ProbeAsync(ports.Scylla, ct).ConfigureAwait(false)
            && await ProbeAsync(ports.Cassandra, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ProbeAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PortProbeTimeout);
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task LaunchAppHostAsync(BenchPorts ports, CancellationToken ct)
    {
        var repoRoot = ResolveRepoRoot();
        var appHostCsproj = Path.Combine(repoRoot, "hosts", "Interfold.AppHost", "Interfold.AppHost.csproj");
        if (!File.Exists(appHostCsproj))
            throw new FileNotFoundException(
                $"Test-bench launcher could not locate the AppHost csproj at '{appHostCsproj}'. " +
                "The bench requires the AppHost project to be present in the repo tree.",
                appHostCsproj);

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(appHostCsproj);
        psi.ArgumentList.Add("--");
        foreach (var arg in BuildAppHostConfigArgs(ports))
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrBuf = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // Echo the child's stdout to the parent so operators can diagnose slow boots.
            Console.WriteLine(e.Data);
            if (e.Data.StartsWith(ReadyLinePrefix, StringComparison.Ordinal))
                readyTcs.TrySetResult(true);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrBuf.AppendLine(e.Data);
            Console.Error.WriteLine(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(LauncherTimeout);
            using var reg = timeoutCts.Token.Register(() => readyTcs.TrySetCanceled(timeoutCts.Token));
            var exitTask = proc.WaitForExitAsync(timeoutCts.Token);
            var completed = await Task.WhenAny(readyTcs.Task, exitTask).ConfigureAwait(false);
            if (completed == exitTask)
            {
                await exitTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Test-bench AppHost exited (code={proc.ExitCode}) before emitting '{ReadyLinePrefix}'. " +
                    $"stderr:{Environment.NewLine}{stderrBuf}");
            }
            await readyTcs.Task.ConfigureAwait(false);

            // AppHost prints ready and StopApplication'd itself in TestBenchReadyEmitter;
            // wait briefly for the graceful shutdown, then kill if it drags its feet.
            try
            {
                using var gracefulCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                gracefulCts.CancelAfter(TimeSpan.FromSeconds(30));
                await proc.WaitForExitAsync(gracefulCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException(
                $"Test-bench AppHost did not emit '{ReadyLinePrefix}' within {LauncherTimeout.TotalMinutes:0} minutes. " +
                $"stderr:{Environment.NewLine}{stderrBuf}");
        }
    }

    private static IEnumerable<string> BuildAppHostConfigArgs(BenchPorts ports)
    {
        // Use CommandLine configuration syntax `--key=value` (Microsoft.Extensions.Configuration.CommandLine
        // parses these into IConfiguration). Aspire's DistributedApplicationBuilder inherits
        // the default config providers so these overrides land on the same keys the
        // InterfoldAppHost reads.
        yield return $"--{AppHostParameterKeys.TestBenchMode}=true";
        yield return $"--{AppHostParameterKeys.IncludeApi}=false";
        yield return $"--{AppHostParameterKeys.IncludeWeb}=false";
        yield return $"--{AppHostParameterKeys.IncludeDashboard}=false";
        yield return $"--{AppHostParameterKeys.PersistentContainers}=true";
        yield return $"--{AppHostParameterKeys.IncludePostgres}=true";
        yield return $"--{AppHostParameterKeys.IncludeScylla}=true";
        yield return $"--{AppHostParameterKeys.IncludeCassandra}=true";
        yield return $"--{AppHostParameterKeys.PortsPostgres}={ports.Postgres.ToString(CultureInfo.InvariantCulture)}";
        yield return $"--{AppHostParameterKeys.PortsScylla}={ports.Scylla.ToString(CultureInfo.InvariantCulture)}";
        yield return $"--{AppHostParameterKeys.PortsCassandra}={ports.Cassandra.ToString(CultureInfo.InvariantCulture)}";
        yield return $"--{AppHostParameterKeys.PostgresUser}={TestDbCredentials.PostgresAppUser}";
        yield return $"--{AppHostParameterKeys.PostgresPassword}={TestDbCredentials.PostgresAppPassword}";
        yield return $"--{AppHostParameterKeys.PostgresInitPassword}={TestDbCredentials.PostgresInitPassword}";
        yield return $"--{AppHostParameterKeys.PostgresDb}={DbInitHelper.DefaultPostgresDb}";
        yield return $"--{AppHostParameterKeys.ScyllaUser}={TestDbCredentials.ScyllaAppUser}";
        yield return $"--{AppHostParameterKeys.ScyllaPassword}={TestDbCredentials.ScyllaAppPassword}";
        yield return $"--{AppHostParameterKeys.EncryptionPrivateKey}=TEST";
    }

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> until it finds
    /// <c>Interfold.slnx</c>. Every test host runs out of a
    /// <c>tests/{unit|integration}/&lt;Project&gt;/bin/&lt;Cfg&gt;/net10.0/</c> layout that
    /// sits somewhere beneath the repo root, so the walk always terminates.</summary>
    public static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Interfold.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate 'Interfold.slnx' walking up from '{AppContext.BaseDirectory}'. " +
            "The test-bench launcher needs the repo root to spawn hosts/Interfold.AppHost.");
    }
}
