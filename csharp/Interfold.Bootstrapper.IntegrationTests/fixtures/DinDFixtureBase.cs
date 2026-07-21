using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using TUnit.Assertions;
using TUnit.Core.Interfaces;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Shared TUnit fixture base for Docker-in-Docker integration tests of the bootstrapper.
/// Each concrete subclass picks a per-distro <see cref="DockerfileName"/>; the lifetime hooks
/// here handle the work that's identical across all distros:
/// <list type="number">
///   <item>Publish the bootstrapper once for the entire test session (cached via <see cref="Lazy{T}"/>).</item>
///   <item>Build the API container image once and save it to a tarball (also session-cached).</item>
///   <item>Build the DinD image for this distro (per-fixture-type cached).</item>
///   <item>Spawn a privileged DinD container, wait for the inner <c>dockerd</c>, then load the
///         saved API image and pre-pull external images.</item>
/// </list>
/// </summary>
public abstract class DinDFixtureBase : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>Mount target inside the DinD container for the published bootstrapper binary.</summary>
    public const string BootstrapperMountPath = "/opt/bootstrapper";

    /// <summary>Mount target for the repo's <c>scripts/</c>, <c>csharp/Interfold.Infrastructure.Postgres/Migrations/</c>, and <c>db/</c>.</summary>
    public const string SupportFilesMountPath = "/opt/support";

    /// <summary>Root directory under which per-test scratch dirs are created.</summary>
    public const string ScratchRoot = "/opt/scratch";

    /// <summary>
    /// Width of the host-port window each test reserves inside the shared DinD's network
    /// namespace. We allocate 6 ports per test (api-http, api-https, web-http, web-https,
    /// postgres, scylla) and round up to 10 so the integer math leaves clean 4-port gaps for
    /// debugging / future expansion. With a starting cursor of <see cref="PortAllocationStart"/>
    /// and a step of 10 we can issue ~4,000 unique blocks before bumping into the ephemeral
    /// range — far more than any plausible suite size.
    /// </summary>
    private const int PortAllocationStride = 10;

    /// <summary>
    /// First base port handed out by <see cref="AllocatePorts"/>. Sits well below the standard
    /// Linux ephemeral range (32768-60999) so we never collide with whatever the inner dockerd
    /// happens to grab for outbound ephemeral sockets.
    /// </summary>
    private const int PortAllocationStart = 25000;

    // First Interlocked.Add(ref _portCursor, PortAllocationStride) call returns PortAllocationStart;
    // we pre-decrement so the first allocation lands exactly on the start value rather than one
    // stride past it.
    private static int _portCursor = PortAllocationStart - PortAllocationStride;

    /// <summary>Per-test stdout/stderr capture; flushed by <c>CaptureFailureArtifactsAsync</c>.</summary>
    public ConcurrentDictionary<string, StringBuilder> BootstrapperLogs { get; } = new();

    /// <summary>Per-test scratch directory metadata, keyed by test name. Populated by <c>CreateScratchAsync</c>.</summary>
    public ConcurrentDictionary<string, DinDScratch> Scratches { get; } = new();

    private IContainer? _dinD;
    private string? _publishedBootstrapperDir;

    /// <summary>Name (without directory) of the Dockerfile to use, e.g. <c>Dockerfile.ubuntu-dind</c>.</summary>
    protected abstract string DockerfileName { get; }

    /// <summary>If false, the fixture skips the API image pre-load / external image pre-pull (used by the Alpine negative-path).</summary>
    protected virtual bool PreloadImages => true;

    /// <summary>
    /// Extra registry images the concrete fixture wants pre-pulled into the inner Docker daemon,
    /// in addition to the baseline (<c>timescale/timescaledb:latest-pg18</c> +
    /// <c>scylladb/scylla:2026.1</c>) that every fixture shares. Only consulted when
    /// <see cref="PreloadImages"/> is true. Empty by default; the cassandra-mode fixture
    /// overrides this to include <c>cassandra:5</c> so the local Cassandra image build
    /// (<c>db/cassandra/Dockerfile</c>, <c>FROM cassandra:5</c>) doesn't have to fetch its
    /// base from the network on every test run.
    /// </summary>
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

        // Provision the in-container scratch root once. Each test will mkdir its own
        // subdirectory underneath so they can run in parallel without stomping on each other.
        await ExecAsync(["mkdir", "-p", ScratchRoot]).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a fresh per-test scratch dir inside the shared DinD container, drops the test config
    /// into it, and returns the in-container paths. Tests pass the scratch's <c>OutputDir</c> and
    /// <c>ConfigPath</c> to the bootstrapper via <c>--output-dir</c> / <c>--config</c> so each test
    /// has its own deploy directory and they can run concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scratch dir itself is used as the bootstrapper output dir (no nested <c>deploy/</c>),
    /// because <c>docker compose -f &lt;file&gt;</c> derives its project name from the parent
    /// directory's basename. Giving every test a uniquely-named parent dir means each
    /// <c>compose up</c> creates its own isolated container/volume/network namespace - tests that
    /// invoke <c>bootstrap</c> never share state with each other or with other tests.
    /// </para>
    /// <para>
    /// On top of the per-test scratch dir we also allocate a private 6-port window (api-http,
    /// api-https, web-http, web-https, postgres, scylla) via <see cref="AllocatePorts"/> and
    /// substitute it into the <c>ports</c> block of the supplied JSON template before copying it
    /// into the DinD. Without this rewrite every test would emit a compose file binding the same
    /// host ports inside the shared DinD's single network namespace and concurrent
    /// <c>compose up</c> calls would collide on "port already allocated". The per-test rewrite is
    /// what lets the assembly drop the <c>*-compose-up</c> <c>NotInParallel</c> serialisers.
    /// </para>
    /// </remarks>
    public async Task<DinDScratch> CreateScratchAsync(string testName, string configHostPath, CancellationToken ct = default)
    {
        if (_dinD is null) throw new InvalidOperationException("Fixture not initialized.");

        // Compose project names must be lowercase ASCII. We lowercase the test name and drop any
        // remaining illegal chars to avoid `docker compose` rejecting the implicit project name.
        var sanitized = string.Concat(testName.ToLowerInvariant()
            .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'));
        // Short random suffix so re-runs of the same test name within a session still get a clean
        // workspace, and so multiple `[DataDrivenTest]` invocations of one method don't collide.
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
        // Stash the scratch so [After(Test)] hooks can recover it from the fixture by test name
        // without needing to mutate test-class instance fields (which TUnit discourages).
        Scratches[testName] = scratch;
        return scratch;
    }

    /// <summary>
    /// Hands out a fresh 6-port allocation for a single test. Uses <see cref="Interlocked.Add(ref int, int)"/>
    /// so concurrent fixtures (Ubuntu + Fedora + bare-prereqs) draw from the same monotonic
    /// counter without any locking. The first call returns ports starting at
    /// <see cref="PortAllocationStart"/>; each subsequent call advances by <see cref="PortAllocationStride"/>.
    /// </summary>
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

    /// <summary>
    /// Returns a copy of <paramref name="configBytes"/> with the top-level <c>ports</c> object
    /// replaced by the supplied <paramref name="ports"/>. Uses <see cref="JsonNode"/> so the
    /// surrounding config (deployment, databaseMode, apiImage, oauth) is preserved verbatim.
    /// </summary>
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

    /// <summary>
    /// Executes a command inside the DinD container. Surfaces non-zero exit codes as exceptions
    /// for tests that just want a sanity-check, but returns the full result for tests that need
    /// to assert on output.
    /// </summary>
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

    /// <summary>
    /// Convenience wrapper over <see cref="CopyOutAsync(string, CancellationToken)"/> that
    /// reads the per-scratch <c>secrets/secrets.json</c> and returns it as a UTF-8 string.
    /// Every test that needs to grep / compare / rotate the secrets file was previously
    /// open-coding <c>Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{scratch.OutputDir}/secrets/secrets.json"))</c>
    /// — this helper is the single fix-forward point if the path or format ever changes.
    /// </summary>
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

    /// <summary>
    /// Counts files matching a glob under the scratch's output dir. The <paramref name="relativePathOrGlob"/>
    /// is joined onto <see cref="DinDScratch.OutputDir"/> in-container — passing
    /// <c>"{scratch.OutputDir}/backups/postgres/*.dump"</c> is a bug (the literal <c>{scratch.OutputDir}</c>
    /// string reaches <c>ls</c> and matches nothing), so this signature takes the scratch as a first-class
    /// parameter to eliminate that footgun (see sibling <see cref="ReadSecretsFieldAsync"/>). Callers pass
    /// the trailing relative path only, e.g. <c>"backups/postgres/*.dump"</c>.
    /// </summary>
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
        // CopyAsync(content, path, userId, groupId, fileMode, ct) - mode 0600 (owner rw only).
        const UnixFileModes Mode0600 = UnixFileModes.UserRead | UnixFileModes.UserWrite;
        await _dinD.CopyAsync(bytes, containerPath, 0, 0, Mode0600, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a <c>psql</c> command against the <c>msg-db</c> service of the compose project at
    /// <paramref name="composeFile"/> inside the DinD container. Concentrates the
    /// <c>docker compose -f … exec -T [-e PGPASSWORD=…] msg-db psql -U … -d … -h … …</c> shape
    /// that every DB-init / restore integration test was hand-rolling with slight variations.
    /// <para>
    /// <paramref name="quotedSql"/> must include its outer shell quoting (single or double).
    /// <paramref name="password"/>, when supplied, is forwarded verbatim, so both literal
    /// values (<c>"abc123"</c>) and shell expressions (<c>"$ADMIN_PW"</c>, subshells) work.
    /// Set <paramref name="softFail"/> to append <c>2&gt;&amp;1 || true</c> for probes that
    /// deliberately expect a non-zero psql exit (auth-refused, permission-denied etc.).
    /// </para>
    /// </summary>
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
        // NOTE: the "PGPASSWORD" env-var name is intentionally kept as a string literal here rather
        // than referencing DatabaseArchiveStreamer.PgPasswordEnvVar. This project's csproj holds the
        // bootstrapper reference with ReferenceOutputAssembly="false" + ExcludeAssets="all" (see
        // Interfold.Bootstrapper.IntegrationTests.csproj) so nothing from Interfold.Bootstrapper.dll
        // is compile-linked into the test binary — the fixture shells out to `dotnet publish`
        // instead. Also, "PGPASSWORD" is a fixed Postgres-client protocol name, not a value we
        // control, so drift-clearance is not a concern.
        var pwPrefix = password is null ? string.Empty : $"-e PGPASSWORD={password} ";
        var hostArg = host is null ? string.Empty : $"-h {host} ";
        var tail = softFail ? " 2>&1 || true" : string.Empty;
        var cmd = $"docker compose -f {composeFile} exec -T {pwPrefix}msg-db " +
                  $"psql -U {user} -d {database} {hostArg}{psqlFlags} {quotedSql}{tail}";
        return ExecAsync(["sh", "-c", cmd], ct);
    }

    /// <summary>
    /// Runs a <c>cqlsh</c> command against the <c>scylla</c> service of the compose project at
    /// <paramref name="composeFile"/> inside the DinD container. Concentrates the
    /// <c>docker compose -f … exec -T scylla cqlsh -u … -p … -e "…" 2&gt;&amp;1 || true</c>
    /// shape shared by every Scylla auth/role invariant test.
    /// <para>
    /// <paramref name="quotedCqlExpression"/> must include its outer double quotes.
    /// <paramref name="password"/> is forwarded verbatim (literal values, shell variables,
    /// and <c>"$(...)"</c> subshells all work). <paramref name="softFail"/> defaults to
    /// <c>true</c> because Scylla auth probes typically expect a non-zero cqlsh exit and
    /// grep the stdout/stderr for the failure mode.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Runs the bootstrapper binary inside the DinD container with the given args, capturing
    /// stdout/stderr into <see cref="BootstrapperLogs"/> keyed by <paramref name="testName"/>.
    /// </summary>
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

    /// <summary>
    /// Runs an arbitrary bootstrapper subcommand against an already-created scratch, with the
    /// three flags that every test would otherwise repeat (<c>--config</c>, <c>--output-dir</c>,
    /// <c>--non-interactive</c>) pre-populated. Does NOT assert on exit code — callers make
    /// their own success/failure assertions since different sites expect different exit codes
    /// (health-timeout wants non-zero; secondary <c>bootstrap</c> re-runs want zero; etc.).
    /// </summary>
    /// <remarks>
    /// Use this instead of hand-rolling <see cref="RunBootstrapperAsync"/> whenever a test is
    /// invoking a single subcommand (<c>install-service</c>, <c>update-images</c>, <c>backup</c>,
    /// <c>restore</c>, <c>up</c>, <c>rotate-secrets</c>, <c>rotate-certs</c>, second <c>bootstrap</c>,
    /// etc.) against a scratch it already holds. For the primary <c>bootstrap</c>/<c>publish</c>
    /// at the top of a test body, prefer <see cref="BootstrapAsync"/>/<see cref="PublishAsync"/>
    /// which also create the scratch and assert exit zero.
    /// </remarks>
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

    /// <summary>
    /// Drives a full bootstrap against a fresh scratch and returns the in-container compose
    /// file path. Each test calls this once at the top of its body, then issues its probes.
    /// </summary>
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

    /// <summary>
    /// Drives a publish against a fresh scratch and returns the in-container compose
    /// file path. Each test calls this once at the top of its body, then issues its probes.
    /// </summary>
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

    /// <summary>
    /// Dumps the per-test scratch directory (with secret values redacted), the docker compose
    /// log tail, and the captured bootstrapper stdout/stderr for the named test to
    /// <c>{bin}/test-artifacts/{testName}/</c>. Called from each test class's <c>[After(Test)]</c>
    /// hook when the test fails. Returns silently if no scratch was created for the test (e.g.
    /// the test failed before calling <c>CreateScratchAsync</c>).
    /// </summary>
    public async Task CaptureFailureArtifactsAsync(string testName, CancellationToken ct = default)
    {
        if (!Scratches.TryGetValue(testName, out var scratch))
        {
            return;
        }
        await CaptureFailureArtifactsAsync(scratch, testName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tears down any docker compose stack a test brought up, releasing the test's reserved
    /// host-port window inside the DinD network namespace so reruns within the same session can
    /// allocate fresh ports without the previous bind lingering. Idempotent: silently no-ops if
    /// the test never created a scratch or never ran compose up.
    /// </summary>
    /// <remarks>
    /// We share one DinD container across the whole test session. The per-test port allocator in
    /// <see cref="CreateScratchAsync"/> guarantees no two concurrent tests bind the same host
    /// ports, but if a test exits without `compose down` those ports stay bound until the DinD
    /// itself goes away. Tearing down in [After(Test)] keeps the port pool tidy and means
    /// failing-then-rerunning a test never trips over its own stale binds.
    /// </remarks>
    public async Task TearDownComposeAsync(string testName, CancellationToken ct = default)
    {
        if (_dinD is null) return;
        if (!Scratches.TryGetValue(testName, out var scratch)) return;

        // -v drops named volumes too so a follow-up test starts from a clean Postgres / Scylla
        // data volume. --remove-orphans cleans up any service that was renamed between tests.
        // We swallow errors and the result here: the only thing this needs to do is release
        // host ports, and any failure here would just be surfaced again on the next compose up.
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

        // 1. Snapshot the test's deploy directory. Redact the secrets file before writing locally.
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

        // 2. Compose logs for this test's project (compose namespaces by file's parent directory name).
        var logs = await ExecAsync(
            ["sh", "-c", $"docker compose -f {scratch.OutputDir}/docker-compose.yaml logs --tail 200 || true"], ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dest, "compose-logs.txt"), logs.Stdout + "\n--- stderr ---\n" + logs.Stderr, ct).ConfigureAwait(false);

        // 3. Bootstrapper stdout/stderr captured during the failing test.
        if (BootstrapperLogs.TryGetValue(testName, out var sb))
        {
            await File.WriteAllTextAsync(Path.Combine(dest, "bootstrapper.log"), sb.ToString(), ct).ConfigureAwait(false);
        }
    }

    private static string RedactSecrets(byte[] raw)
    {
        // Cheap textual scrub - the secrets file is small enough that the perf cost is irrelevant,
        // and using System.Text.Json directly would need the source-gen context from the bootstrapper
        // project which we deliberately don't reference.
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
        // The bootstrapper expects scripts/, csharp/Interfold.Infrastructure.Postgres/Migrations/,
        // and db/ relative to its own directory. We bind the repo root directly so the layout
        // matches the release tarball when the bootstrapper resolves its anchor.
        return RepoRoot.Path;
    }
}

/// <summary>Result of an <c>ExecAsync</c> call inside the DinD container.</summary>
public readonly record struct ExecResult(long ExitCode, string Stdout, string Stderr);

/// <summary>
/// In-container paths and bound host ports that uniquely identify a test's scratch workspace.
/// The fixture issues one of these per test so parallel tests don't share <c>/opt/deploy</c>
/// or contend for the same host port window inside the DinD's network namespace.
/// </summary>
/// <param name="Root">The scratch root, e.g. <c>/opt/scratch/IsIdempotentOnRerun-7a1b3c0d</c>.</param>
/// <param name="OutputDir">Where the bootstrapper emits compose/secrets/certs (<c>{Root}/deploy</c>).</param>
/// <param name="ConfigPath">Per-test copy of <c>interfold.bootstrap.json</c> (<c>{Root}/interfold.bootstrap.json</c>).</param>
/// <param name="Ports">Per-test host-port allocation embedded into the per-test config's <c>ports</c> block.</param>
public readonly record struct DinDScratch(string Root, string OutputDir, string ConfigPath, DinDPortAllocation Ports)
{
    /// <summary>
    /// In-container path to the bootstrapper-emitted <c>secrets/secrets.json</c>. Convenient
    /// shorthand for the <c>{OutputDir}/secrets/secrets.json</c> fragment repeated across
    /// nearly every secret-adjacent integration test — see
    /// <c>DinDFixtureBase.ReadSecretsJsonAsync</c>.
    /// </summary>
    public string SecretsJsonPath => $"{OutputDir}/secrets/secrets.json";
}

/// <summary>
/// 6-port allocation issued per test by <see cref="DinDFixtureBase.AllocatePorts"/>. The values
/// are baked into the per-test <c>interfold.bootstrap.json</c> via the
/// <c>CreateScratchAsync</c> rewrite step, then read back through
/// <see cref="Interfold.Bootstrapper.Configuration.PortsSection"/> when the bootstrapper runs.
/// </summary>
public readonly record struct DinDPortAllocation(
    int ApiHttp,
    int ApiHttps,
    int WebHttp,
    int WebHttps,
    int Postgres,
    int Scylla);
