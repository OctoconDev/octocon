using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>DinD fixture base for bootstrapper integration tests. Subclasses pick a
/// <see cref="DockerfileName"/>; base handles session-cached publish, API-image build,
/// per-fixture DinD build, privileged spawn, and image preload.</summary>
public abstract class DinDFixtureBase : IAsyncInitializer, IAsyncDisposable
{
    public const string BootstrapperMountPath = "/opt/bootstrapper";
    public const string SupportFilesMountPath = "/opt/support";
    public const string ScratchRoot = "/opt/scratch";

    // 6 ports per test rounded to 10 for readable gaps; ~4000 blocks before ephemeral range.
    private const int PortAllocationStride = 10;

    // Below Linux ephemeral range (32768-60999).
    private const int PortAllocationStart = 25000;

    // Pre-decrement so the first Interlocked.Add returns exactly PortAllocationStart.
    private static int _portCursor = PortAllocationStart - PortAllocationStride;

    public ConcurrentDictionary<string, StringBuilder> BootstrapperLogs { get; } = new();
    public ConcurrentDictionary<string, DinDScratch> Scratches { get; } = new();

    private IContainer? _dinD;
    private string? _publishedBootstrapperDir;

    protected abstract string DockerfileName { get; }

    /// <summary>false skips API image preload + external pulls (Alpine negative-path).</summary>
    protected virtual bool PreloadImages => true;

    /// <summary>Extra images to preload beyond the baseline; cassandra fixture uses this
    /// for <c>cassandra:5</c>.</summary>
    protected virtual IReadOnlyList<string> AdditionalPreloadImages => [];

    public virtual async Task InitializeAsync()
    {
        _publishedBootstrapperDir = await BootstrapperBuild.PublishedDirectory.Value.ConfigureAwait(false);
        var apiImageTarPath = PreloadImages
            ? await BootstrapperBuild.ApiImageTarPath.Value.ConfigureAwait(false)
            : null;

        // Build the DinD image once per concrete fixture type. Keyed by the Dockerfile name so the
        // three subclasses each get their own cached image entry.
        var dindImage = await DinDImageCache.GetOrBuildAsync(DockerfileName, FixturesDir).ConfigureAwait(false);

#pragma warning disable CS0618 // Parameterless ctor planned for removal in TC 5.x; the new ctor signature isn't yet stable.
        var builder = new ContainerBuilder()
            .WithImage(dindImage)
            .WithPrivileged(true)
            // Mount the bootstrapper publish output and a tmpfs config dir.
            .WithBindMount(_publishedBootstrapperDir, BootstrapperMountPath, AccessMode.ReadWrite)
            // Mount repo support files needed at compose-up time.
            .WithBindMount(SupportFilesHostPath(), SupportFilesMountPath, AccessMode.ReadOnly);

        if (apiImageTarPath is not null)
        {
            builder = builder.WithBindMount(apiImageTarPath, "/opt/api-image.tar", AccessMode.ReadOnly);
        }

        _dinD = builder.Build();
#pragma warning restore CS0618
        await _dinD.StartAsync().ConfigureAwait(false);

        if (PreloadImages)
        {
            await WaitForInnerDockerAsync().ConfigureAwait(false);
            await ExecAsync(["docker", "load", "-i", "/opt/api-image.tar"]).ConfigureAwait(false);
            await ExecAsync(["docker", "pull", "timescale/timescaledb:latest-pg18"]).ConfigureAwait(false);
            await ExecAsync(["docker", "pull", "scylladb/scylla:2026.1"]).ConfigureAwait(false);
            foreach (var image in AdditionalPreloadImages)
            {
                await ExecAsync(["docker", "pull", image]).ConfigureAwait(false);
            }
        }

        await ExecAsync(["mkdir", "-p", ScratchRoot]).ConfigureAwait(false);
    }

    /// <summary>Per-test scratch dir + uniquely-named 6-port window. The scratch doubles as
    /// the bootstrapper output dir so each <c>compose up</c> gets its own project name
    /// (parent-basename), and port rewrites let concurrent tests share the DinD network
    /// namespace without collisions.</summary>
    public async Task<DinDScratch> CreateScratchAsync(string testName, string configHostPath, CancellationToken ct = default)
    {
        if (_dinD is null) throw new InvalidOperationException("Fixture not initialized.");

        // Compose project names must be lowercase ASCII.
        var sanitized = string.Concat(testName.ToLowerInvariant()
            .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'));
        // Random suffix so name-reuse and [DataDrivenTest] iterations don't collide.
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var dir = $"{ScratchRoot}/{sanitized}-{suffix}";
        var configPath = $"{dir}/interfold.bootstrap.json";

        await ExecAsync(["mkdir", "-p", dir], ct).ConfigureAwait(false);
        var templateBytes = await File.ReadAllBytesAsync(configHostPath, ct).ConfigureAwait(false);
        var ports = AllocatePorts();
        var rewritten = RewriteConfigWithPorts(templateBytes, ports);
        const UnixFileModes Mode0600 = UnixFileModes.UserRead | UnixFileModes.UserWrite;
        await _dinD.CopyAsync(rewritten, configPath, 0, 0, Mode0600, ct).ConfigureAwait(false);

        var scratch = new DinDScratch(dir, dir, configPath, ports);
        // Stash so [After(Test)] hooks can recover by test name without instance-field state.
        Scratches[testName] = scratch;
        return scratch;
    }

    /// <summary>Lock-free monotonic port allocation shared across concurrent fixtures.</summary>
    public static DinDPortAllocation AllocatePorts()
    {
        var basePort = Interlocked.Add(ref _portCursor, PortAllocationStride);
        return new DinDPortAllocation(
            ApiHttp: basePort + 0,
            ApiHttps: basePort + 1,
            WebHttp: basePort + 2,
            WebHttps: basePort + 3,
            Postgres: basePort + 4,
            Scylla: basePort + 5);
    }

    /// <summary>Overwrites the top-level <c>ports</c> object; the rest of the config is
    /// preserved verbatim via <see cref="JsonNode"/>.</summary>
    private static byte[] RewriteConfigWithPorts(byte[] configBytes, DinDPortAllocation ports)
    {
        var root = JsonNode.Parse(configBytes)
            ?? throw new InvalidOperationException("test bootstrap config parsed to null");
        root["ports"] = new JsonObject
        {
            ["apiHttp"] = ports.ApiHttp,
            ["apiHttps"] = ports.ApiHttps,
            ["webHttp"] = ports.WebHttp,
            ["webHttps"] = ports.WebHttps,
            ["postgres"] = ports.Postgres,
            ["scylla"] = ports.Scylla,
        };
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return Encoding.UTF8.GetBytes(json);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dinD is not null)
        {
            await _dinD.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<ExecResult> ExecAsync(IList<string> command, CancellationToken ct = default)
    {
        if (_dinD is null) throw new InvalidOperationException("Fixture not initialized.");
        var result = await _dinD.ExecAsync(command, ct).ConfigureAwait(false);
        return new ExecResult(result.ExitCode ?? -1, result.Stdout ?? string.Empty, result.Stderr ?? string.Empty);
    }

    /// <summary>Copy a file out of the DinD container into a byte buffer.</summary>
    public async Task<byte[]> CopyOutAsync(string containerPath, CancellationToken ct = default)
    {
        if (_dinD is null) throw new InvalidOperationException("Fixture not initialized.");
        return await _dinD.ReadFileAsync(containerPath, ct).ConfigureAwait(false);
    }

    /// <summary>Single fix-forward point for reading secrets.json.</summary>
    public async Task<string> ReadSecretsJsonAsync(DinDScratch scratch, CancellationToken ct = default)
    {
        var bytes = await CopyOutAsync(scratch.SecretsJsonPath, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<string> ReadSecretsFieldAsync(DinDScratch scratch, string field, CancellationToken ct = default)
    {
        var json = await ReadSecretsJsonAsync(scratch, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty(field, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? (prop.GetString() ?? string.Empty) : prop.GetRawText().Trim('"');
        }
        return string.Empty;
    }

    /// <summary>Counts files matching a glob under the scratch's output dir. Callers pass
    /// a trailing relative path (e.g. <c>"backups/postgres/*.dump"</c>); the scratch is
    /// applied here to prevent the literal-<c>{scratch.OutputDir}</c> footgun.</summary>
    public async Task<int> CountFilesAsync(DinDScratch scratch, string relativePathOrGlob, CancellationToken ct = default)
    {
        var fullPath = $"{scratch.OutputDir}/{relativePathOrGlob.TrimStart('/')}";
        var exec = await ExecAsync(["sh", "-c", $"ls -1 {fullPath} 2>/dev/null | wc -l"], ct).ConfigureAwait(false);
        return int.TryParse(exec.Stdout.Trim(), out var count) ? count : 0;
    }

    public async Task<string> CopyOutAsTextAsync(string containerPath, CancellationToken ct = default)
    {
        var bytes = await CopyOutAsync(containerPath, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Copy a host file into the DinD container.</summary>
    public async Task CopyInAsync(string hostPath, string containerPath, CancellationToken ct = default)
    {
        if (_dinD is null) throw new InvalidOperationException("Fixture not initialized.");
        var bytes = await File.ReadAllBytesAsync(hostPath, ct).ConfigureAwait(false);
        const UnixFileModes Mode0600 = UnixFileModes.UserRead | UnixFileModes.UserWrite;
        await _dinD.CopyAsync(bytes, containerPath, 0, 0, Mode0600, ct).ConfigureAwait(false);
    }

    /// <summary>Shared <c>docker compose exec msg-db psql</c> shape. <paramref name="quotedSql"/>
    /// carries its outer shell quoting; <paramref name="password"/> is forwarded verbatim
    /// (literals or shell expressions); <paramref name="softFail"/> appends <c>2&gt;&amp;1 || true</c>
    /// for probes that expect a non-zero exit.</summary>
    public Task<ExecResult> PsqlAsync(
        string composeFile,
        string user,
        string database,
        string quotedSql,
        string? password = null,
        string psqlFlags = "-tAc",
        string? host = "127.0.0.1",
        bool softFail = false,
        CancellationToken ct = default)
    {
        // "PGPASSWORD" stays a literal — the csproj holds the bootstrapper reference with
        // ExcludeAssets="all" (compile-link not allowed), and the env-var name is fixed by
        // the Postgres client protocol, so no drift risk.
        var pwPrefix = password is null ? string.Empty : $"-e PGPASSWORD={password} ";
        var hostArg = host is null ? string.Empty : $"-h {host} ";
        var tail = softFail ? " 2>&1 || true" : string.Empty;
        var cmd = $"docker compose -f {composeFile} exec -T {pwPrefix}msg-db " +
                  $"psql -U {user} -d {database} {hostArg}{psqlFlags} {quotedSql}{tail}";
        return ExecAsync(["sh", "-c", cmd], ct);
    }

    /// <summary>Shared <c>docker compose exec scylla cqlsh</c> shape. <paramref name="softFail"/>
    /// defaults to true because auth probes typically expect a non-zero exit and grep the output.</summary>
    public Task<ExecResult> CqlshAsync(
        string composeFile,
        string user,
        string password,
        string quotedCqlExpression,
        bool softFail = true,
        CancellationToken ct = default)
    {
        var tail = softFail ? " 2>&1 || true" : string.Empty;
        var cmd = $"docker compose -f {composeFile} exec -T scylla " +
                  $"cqlsh -u {user} -p {password} -e {quotedCqlExpression}{tail}";
        return ExecAsync(["sh", "-c", cmd], ct);
    }

    /// <summary>Captures stdout/stderr into <see cref="BootstrapperLogs"/>.</summary>
    public async Task<ExecResult> RunBootstrapperAsync(string testName, IList<string> args, CancellationToken ct = default)
    {
        var cmd = new List<string> { $"{BootstrapperMountPath}/interfold-bootstrap" };
        cmd.AddRange(args);
        var result = await ExecAsync(cmd, ct).ConfigureAwait(false);

        var sb = BootstrapperLogs.GetOrAdd(testName, _ => new StringBuilder());
        sb.AppendLine($"$ {string.Join(' ', cmd)}");
        sb.AppendLine($"# exit={result.ExitCode}");
        sb.AppendLine("--- stdout ---");
        sb.AppendLine(result.Stdout);
        sb.AppendLine("--- stderr ---");
        sb.AppendLine(result.Stderr);
        return result;
    }

    /// <summary>Runs a subcommand against an existing scratch with the three shared flags
    /// pre-populated. Does not assert on exit code — callers decide (health-timeout expects
    /// non-zero, re-runs expect zero, etc.).</summary>
    public Task<ExecResult> RunOnScratchAsync(
        DinDScratch scratch,
        string testName,
        string command,
        params string[] extraArgs)
    {
        var args = new List<string>(4 + extraArgs.Length)
        {
            command,
            "--config", scratch.ConfigPath,
            "--output-dir", scratch.OutputDir,
            "--non-interactive",
        };
        args.AddRange(extraArgs);
        return RunBootstrapperAsync(testName, args);
    }

    /// <summary>Full bootstrap against a fresh scratch; returns the in-container compose path.</summary>
    public async Task<(DinDScratch Scratch, string ComposeFile)> BootstrapAsync(string testName, string configPath, params string[] extraArgs)
    {
        var scratch = await CreateScratchAsync(testName, configPath);
        var args = new List<string> { "bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive", "--skip-prereqs" };
        args.AddRange(extraArgs);
        var result = await RunBootstrapperAsync(testName, args);
        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"bootstrap must succeed before invariants can be checked: {result.Stderr}");
        return (scratch, $"{scratch.OutputDir}/docker-compose.yaml");
    }

    /// <summary>Publish against a fresh scratch; returns the in-container compose path.</summary>
    public async Task<(DinDScratch Scratch, string ComposeFile)> PublishAsync(string testName, string configPath, params string[] extraArgs)
    {
        var scratch = await CreateScratchAsync(testName, configPath);
        var args = new List<string> { "publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive" };
        args.AddRange(extraArgs);
        var result = await RunBootstrapperAsync(testName, args);
        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"publish must succeed: {result.Stderr}");
        return (scratch, $"{scratch.OutputDir}/docker-compose.yaml");
    }

    /// <summary>Dumps redacted scratch + compose log tail + bootstrapper stdout/stderr to
    /// <c>{bin}/test-artifacts/{testName}/</c>. No-op if no scratch was created.</summary>
    public async Task CaptureFailureArtifactsAsync(string testName, CancellationToken ct = default)
    {
        if (!Scratches.TryGetValue(testName, out var scratch))
        {
            return;
        }
        await CaptureFailureArtifactsAsync(scratch, testName, ct).ConfigureAwait(false);
    }

    /// <summary>Releases the test's reserved host-port window in the shared DinD so reruns
    /// don't trip over stale binds. Idempotent.</summary>
    public async Task TearDownComposeAsync(string testName, CancellationToken ct = default)
    {
        if (_dinD is null) return;
        if (!Scratches.TryGetValue(testName, out var scratch)) return;

        // -v: clean data volumes; --remove-orphans: catch renamed services. Errors swallowed
        // — real issues resurface on the next compose up.
        try
        {
            await ExecAsync(
                ["sh", "-c", $"docker compose -f {scratch.OutputDir}/docker-compose.yaml down -v --remove-orphans --timeout 10 2>&1 || true"],
                ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort cleanup; the next compose up will report any real issue
        }
    }

    private async Task CaptureFailureArtifactsAsync(DinDScratch scratch, string testName, CancellationToken ct)
    {
        var sanitized = string.Concat(testName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var dest = Path.Combine(AppContext.BaseDirectory, "test-artifacts", sanitized);
        Directory.CreateDirectory(dest);

        // Snapshot the deploy directory; redact secrets before writing locally.
        var listing = await ExecAsync(["sh", "-c", $"find {scratch.OutputDir} -type f 2>/dev/null || true"], ct).ConfigureAwait(false);
        foreach (var path in listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var content = await CopyOutAsync(path, ct).ConfigureAwait(false);
                var rel = path.StartsWith(scratch.OutputDir + "/") ? path[(scratch.OutputDir.Length + 1)..] : path.TrimStart('/');
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (rel.EndsWith("secrets.json", StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(target, RedactSecrets(content), ct).ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllBytesAsync(target, content, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // best-effort capture; one unreadable file shouldn't sink the rest
            }
        }

        var logs = await ExecAsync(
            ["sh", "-c", $"docker compose -f {scratch.OutputDir}/docker-compose.yaml logs --tail 200 || true"], ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dest, "compose-logs.txt"), logs.Stdout + "\n--- stderr ---\n" + logs.Stderr, ct).ConfigureAwait(false);

        if (BootstrapperLogs.TryGetValue(testName, out var sb))
        {
            await File.WriteAllTextAsync(Path.Combine(dest, "bootstrapper.log"), sb.ToString(), ct).ConfigureAwait(false);
        }
    }

    private static string RedactSecrets(byte[] raw)
    {
        // Textual scrub; using System.Text.Json would drag in the bootstrapper source-gen context.
        var text = Encoding.UTF8.GetString(raw);
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"""(postgresPassword|scyllaPassword|scyllaAdminPassword|encryptionPrivateKeyB64|encryptionPepper|leafPfxPassword)""\s*:\s*""[^""]*""",
            "\"$1\": \"***\"");
    }

    private async Task WaitForInnerDockerAsync()
    {
        const int maxAttempts = 60;
        for (var i = 0; i < maxAttempts; i++)
        {
            var probe = await ExecAsync(["docker", "info"]).ConfigureAwait(false);
            if (probe.ExitCode == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        throw new TimeoutException("Inner dockerd inside the DinD fixture failed to become ready within 60 seconds.");
    }

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string SupportFilesHostPath()
    {
        // Bind the repo root so the bootstrapper's anchor sees the same layout as the release tarball.
        return RepoRoot.Path;
    }
}

/// <summary>Result of a DinD ExecAsync call.</summary>
public readonly record struct ExecResult(long ExitCode, string Stdout, string Stderr);

/// <summary>Per-test scratch workspace inside the shared DinD.</summary>
public readonly record struct DinDScratch(string Root, string OutputDir, string ConfigPath, DinDPortAllocation Ports)
{
    public string SecretsJsonPath => $"{OutputDir}/secrets/secrets.json";
}

/// <summary>Per-test 6-port allocation baked into the per-test config.</summary>
public readonly record struct DinDPortAllocation(
    int ApiHttp,
    int ApiHttps,
    int WebHttp,
    int WebHttps,
    int Postgres,
    int Scylla);
