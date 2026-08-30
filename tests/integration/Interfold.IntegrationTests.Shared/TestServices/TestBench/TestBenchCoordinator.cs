extern alias AppHost;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection; // AssemblyMetadataAttribute; System.Reflection.Assembly is qualified inline (collides with TUnit.HookType.Assembly).
using AppHostRepoPaths = AppHost::Interfold.AppHost.AppHostRepoPaths;
using AspireResourceFailureDiagnostics = AppHost::Interfold.AppHost.AspireResourceFailureDiagnostics;
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

    /// <summary>Brief state mutations (attach / detach / claim launcher). Must stay short —
    /// long work (AppHost launch, migrations) runs outside the lock so parallel test
    /// assemblies on a high-core machine don't burn the wait budget.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ApplyOnceLockTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LauncherTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PortPollInterval = TimeSpan.FromMilliseconds(500);

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
    /// <remarks>
    /// The advisory lock is held only for short state mutations. AppHost launch runs
    /// outside the lock: a <see cref="TestBenchLeaseState.Launching"/> marker elects a
    /// single launcher while peers poll ports (and re-elect if the launcher dies or clears
    /// the marker). Holding the lock across launch was the failure mode on high-core
    /// <c>dotnet test</c> runs (60s waiters vs multi-minute boot).
    /// </remarks>
    public static async Task<BenchConnectionInfo> EnsureRunningAsync(CancellationToken ct)
    {
        var ports = DefaultPorts;
        var deadline = DateTime.UtcNow + LauncherTimeout;
        var launchedThisCall = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {LauncherTimeout.TotalMinutes:0} minutes waiting for the test-bench " +
                    $"(pg={ports.Postgres}, scylla={ports.Scylla}, cassandra={ports.Cassandra}).");
            }

            bool shouldLaunch;
            using (await TestBenchLease.AcquireAsync(LockTimeout, ct).ConfigureAwait(false))
            {
                var state = TestBenchLease.ReadState();
                state.Users = TestBenchLease.PruneStale(state.Users);
                if (state.Launching is { } launching && !TestBenchLease.IsAlive(launching))
                    state.Launching = null;

                if (await TestBenchIdleReaper.RunIfIdleAsync(state, ct).ConfigureAwait(false))
                    state = new TestBenchLeaseState();

                ports = state.Ports ?? DefaultPorts;

                if (await ArePortsHotAsync(ports, ct).ConfigureAwait(false))
                {
                    state.Ports = ports;
                    state.Launching = null;
                    state.LastAttachUtc = DateTime.UtcNow;
                    state.Users.Add(TestBenchLease.CurrentUser());
                    TestBenchLease.WriteState(state);
                    return BuildConnectionInfo(ports);
                }

                shouldLaunch = state.Launching is null;
                if (shouldLaunch)
                {
                    state.Launching = TestBenchLease.CurrentUser("test-bench-launcher");
                    state.Ports = ports;
                    TestBenchLease.WriteState(state);
                }
                else
                {
                    TestBenchLease.WriteState(state);
                }
            }

            if (shouldLaunch)
            {
                Console.WriteLine("[test-bench] cold ports; launching AppHost to boot containers");
                try
                {
                    await DockerMemoryPreflight.EnsureAdequateAsync(ct).ConfigureAwait(false);
                    ScyllaRackDcMountPaths.VerifyAllPresent(ResolveRepoRoot());
                    await LaunchAppHostAsync(ports, ct).ConfigureAwait(false);
                }
                catch
                {
                    await ClearLaunchingMarkerAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                launchedThisCall = true;
                break;
            }

            Console.WriteLine("[test-bench] cold ports; waiting for peer AppHost launcher");
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                continue;

            var peerOutcome = await WaitForPortsOrLauncherAbandonedAsync(ports, remaining, ct).ConfigureAwait(false);
            if (peerOutcome == PeerWaitOutcome.PortsHot)
                break;

            Console.WriteLine("[test-bench] peer launcher abandoned; retrying claim");
        }

        return await FinishAttachAsync(ports, wipeMigrations: launchedThisCall, ct).ConfigureAwait(false);
    }

    private static async Task<BenchConnectionInfo> FinishAttachAsync(
        BenchPorts ports, bool wipeMigrations, CancellationToken ct)
    {
        using var _ = await TestBenchLease.AcquireAsync(LockTimeout, ct).ConfigureAwait(false);
        var state = TestBenchLease.ReadState();
        state.Users = TestBenchLease.PruneStale(state.Users);

        if (!await ArePortsHotAsync(ports, ct).ConfigureAwait(false))
        {
            state.Launching = null;
            TestBenchLease.WriteState(state);
            throw new TimeoutException(
                $"Test-bench ports (pg={ports.Postgres}, scylla={ports.Scylla}, cassandra={ports.Cassandra}) " +
                "were not accepting connections after AppHost launch / peer wait.");
        }

        // Fresh containers → recorded migration markers are stale.
        if (wipeMigrations)
            state.MigrationsApplied.Clear();

        state.Ports = ports;
        state.Launching = null;
        state.LastAttachUtc = DateTime.UtcNow;
        state.Users.Add(TestBenchLease.CurrentUser());
        TestBenchLease.WriteState(state);
        return BuildConnectionInfo(ports);
    }

    private enum PeerWaitOutcome
    {
        PortsHot,
        LauncherAbandoned,
    }

    /// <summary>Peer wait: ports becoming hot wins; a cleared/dead <c>Launching</c> marker
    /// means we should loop and try to claim the launcher role ourselves.</summary>
    private static async Task<PeerWaitOutcome> WaitForPortsOrLauncherAbandonedAsync(
        BenchPorts ports, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await ArePortsHotAsync(ports, ct).ConfigureAwait(false))
                return PeerWaitOutcome.PortsHot;

            var state = TestBenchLease.ReadState();
            if (state.Launching is null
                || !TestBenchLease.IsAlive(state.Launching))
            {
                return PeerWaitOutcome.LauncherAbandoned;
            }

            await Task.Delay(PortPollInterval, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out waiting {timeout.TotalMinutes:0.##} minutes for test-bench ports " +
            $"(pg={ports.Postgres}, scylla={ports.Scylla}, cassandra={ports.Cassandra}) " +
            "after a peer claimed the AppHost launcher role.");
    }

    private static async Task ClearLaunchingMarkerAsync(CancellationToken ct)
    {
        try
        {
            using var _ = await TestBenchLease.AcquireAsync(LockTimeout, ct).ConfigureAwait(false);
            var state = TestBenchLease.ReadState();
            state.Launching = null;
            TestBenchLease.WriteState(state);
        }
        catch
        {
            // Best-effort — a stuck marker is cleared by IsAlive prune on the next attach.
        }
    }

    /// <summary>Cross-process one-shot for idempotent bench-setup work (DB migrations,
    /// keyspace seeds, etc.). The delegate is invoked exactly once per bench-lifetime;
    /// concurrent callers elect a single worker, then poll the completed marker.</summary>
    /// <remarks>
    /// The file lock is held only for claim / complete mutations — never across
    /// <paramref name="work"/>. Scylla/Cassandra seed+migrate can run for minutes; holding
    /// <c>bench.lock</c> that long caused peer assemblies to hit
    /// <see cref="ApplyOnceLockTimeout"/> while waiting to attach. Exclusion for
    /// non-concurrency-safe seeds (e.g. <c>CREATE DATABASE</c>) comes from
    /// <see cref="TestBenchLeaseState.MigrationsInProgress"/>, same pattern as
    /// <see cref="TestBenchLeaseState.Launching"/>.
    /// </remarks>
    public static async Task ApplyOnceAsync(string key, Func<Task> work, CancellationToken ct)
    {
        if (!IsOptedIn())
        {
            await work().ConfigureAwait(false);
            return;
        }

        var deadline = DateTime.UtcNow + ApplyOnceLockTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Timed out waiting {ApplyOnceLockTimeout.TotalMinutes:0} minutes for " +
                    $"test-bench ApplyOnce key '{key}' (peer migration still in progress or abandoned).");
            }

            var lockWait = remaining < LockTimeout ? remaining : LockTimeout;
            var claim = await TryClaimApplyOnceAsync(key, lockWait, ct).ConfigureAwait(false);
            if (claim == ApplyOnceClaim.AlreadyDone)
                return;

            if (claim == ApplyOnceClaim.PeerRunning)
            {
                await Task.Delay(PortPollInterval, ct).ConfigureAwait(false);
                var snap = TestBenchLease.ReadState();
                if (snap.MigrationsApplied.TryGetValue(key, out var done) && done)
                    return;
                continue;
            }

            try
            {
                await work().ConfigureAwait(false);
                await CompleteApplyOnceAsync(key, ct).ConfigureAwait(false);
                return;
            }
            catch
            {
                await ClearApplyOnceClaimAsync(key, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private enum ApplyOnceClaim
    {
        AlreadyDone,
        PeerRunning,
        Claimed,
    }

    private static async Task<ApplyOnceClaim> TryClaimApplyOnceAsync(
        string key, TimeSpan lockTimeout, CancellationToken ct)
    {
        using var _ = await TestBenchLease.AcquireAsync(lockTimeout, ct).ConfigureAwait(false);
        var state = TestBenchLease.ReadState();
        if (state.MigrationsApplied.TryGetValue(key, out var applied) && applied)
            return ApplyOnceClaim.AlreadyDone;

        if (state.MigrationsInProgress.TryGetValue(key, out var worker)
            && TestBenchLease.IsAlive(worker))
        {
            return ApplyOnceClaim.PeerRunning;
        }

        state.MigrationsInProgress[key] = TestBenchLease.CurrentUser($"apply-once:{key}");
        TestBenchLease.WriteState(state);
        return ApplyOnceClaim.Claimed;
    }

    private static async Task CompleteApplyOnceAsync(string key, CancellationToken ct)
    {
        using var _ = await TestBenchLease.AcquireAsync(LockTimeout, ct).ConfigureAwait(false);
        var state = TestBenchLease.ReadState();
        state.MigrationsApplied[key] = true;
        state.MigrationsInProgress.Remove(key);
        state.LastAttachUtc = DateTime.UtcNow;
        TestBenchLease.WriteState(state);
    }

    private static async Task ClearApplyOnceClaimAsync(string key, CancellationToken ct)
    {
        try
        {
            using var _ = await TestBenchLease.AcquireAsync(LockTimeout, ct).ConfigureAwait(false);
            var state = TestBenchLease.ReadState();
            state.MigrationsInProgress.Remove(key);
            TestBenchLease.WriteState(state);
        }
        catch
        {
            // Best-effort — a stuck claim is cleared by IsAlive prune on the next claim.
        }
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
        // Skip launchSettings.json — it pins ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL=22223
        // (and AppHost Kestrel URLs) that collide with Infrastructure's legacy AspireFixture
        // when both run under a parallel `dotnet test`. `--no-build` avoids a rebuild race
        // while peer test assemblies hold AppHost output DLLs open.
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-launch-profile");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(appHostCsproj);
        psi.ArgumentList.Add("--");
        foreach (var arg in BuildAppHostConfigArgs(ports))
            psi.ArgumentList.Add(arg);

        AspireProcessEndpoints.ApplyTo(
            psi,
            AspireProcessEndpoints.BenchResourceService,
            AspireProcessEndpoints.BenchOtlp,
            AspireProcessEndpoints.BenchAppUrls);

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
                var detail = await EnrichLaunchFailureAsync(stderrBuf.ToString(), ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Test-bench AppHost exited (code={proc.ExitCode}) before emitting '{ReadyLinePrefix}'. " +
                    $"stderr:{Environment.NewLine}{detail}");
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
            var detail = await EnrichLaunchFailureAsync(stderrBuf.ToString(), ct).ConfigureAwait(false);
            throw new TimeoutException(
                $"Test-bench AppHost did not emit '{ReadyLinePrefix}' within {LauncherTimeout.TotalMinutes:0} minutes. " +
                $"stderr:{Environment.NewLine}{detail}");
        }
    }

    private static async Task<string> EnrichLaunchFailureAsync(string stderr, CancellationToken ct)
    {
        if (stderr.Contains("ready-emitter failed", StringComparison.OrdinalIgnoreCase)
            && stderr.Contains("scylla", StringComparison.OrdinalIgnoreCase))
        {
            var repoRoot = ResolveRepoRoot();
            var diagnosis = await AspireResourceFailureDiagnostics
                .DescribeAsync("scylla", repoRoot, ct)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(stderr)
                ? diagnosis
                : stderr.TrimEnd() + Environment.NewLine + diagnosis;
        }

        return stderr;
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

    public static string ResolveRepoRoot() => AppHostRepoPaths.ResolveRepoRoot();
}
