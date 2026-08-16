using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.IntegrationTests.Shared.TestServices.TestBench;

/// <summary>Advisory cross-process lock + JSON state file backing the auto-managed test
/// bench. The state file records which test-host PIDs currently attach to the bench and
/// the last-attach timestamp used by the idle reaper to decide when to <c>docker rm -f</c>
/// the persistent-lifetime containers.</summary>
/// <remarks>
/// <para>
/// Lock semantics: a plain <see cref="FileStream"/> opened with <see cref="FileShare.None"/>
/// on <c>bench.lock</c>. Cross-platform, best-effort — an OS-level crash leaves the file
/// but not the lock, so a stale lock file is harmless. Retry with a 100 ms backoff up to
/// the caller-supplied timeout.
/// </para>
/// <para>
/// The state file is written whole-file atomically (temp + rename) so a torn write can
/// never leave partial JSON behind. Readers tolerate a missing/empty file by treating it
/// as a fresh lease.
/// </para>
/// </remarks>
public static class TestBenchLease
{
    private const string LeaseDirectoryName = "interfold-test-bench";
    private const string LockFileName = "bench.lock";
    private const string StateFileName = "bench.state.json";

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Test-only override of the lease's base directory. The unit tests
    /// link-compile <c>TestBenchLease.cs</c> into their own assembly so they get their
    /// own writable copy of this setter; production callers never touch it.</summary>
    internal static string? TestOnlyBaseDirectoryOverride { get; set; }

    /// <summary>Returns the lease directory rooted under the current user's temp path.
    /// Created on demand.</summary>
    public static string GetLeaseDirectory()
    {
        var baseDir = TestOnlyBaseDirectoryOverride ?? Path.GetTempPath();
        var dir = Path.Combine(baseDir, LeaseDirectoryName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetLockFilePath() => Path.Combine(GetLeaseDirectory(), LockFileName);

    public static string GetStateFilePath() => Path.Combine(GetLeaseDirectory(), StateFileName);

    /// <summary>Acquires the cross-process advisory lock on <see cref="LockFileName"/>.
    /// Retries every 100 ms until <paramref name="timeout"/> elapses. Throws
    /// <see cref="TimeoutException"/> on timeout so bench init fails loudly rather than
    /// silently sharing the lease.</summary>
    public static async Task<IDisposable> AcquireAsync(TimeSpan timeout, CancellationToken ct)
    {
        var path = GetLockFilePath();
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None gives us cross-process exclusion; the stream itself is
                // deliberately zero-content — we care about the lock, not the bytes.
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose | FileOptions.WriteThrough);
                return new LockHandle(stream);
            }
            catch (IOException ex)
            {
                last = ex;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        throw new TimeoutException(
            $"Failed to acquire test-bench lock at '{path}' within {timeout.TotalSeconds:0}s. " +
            "Last error: " + (last?.Message ?? "unknown"));
    }

    /// <summary>Loads the lease state from disk. Returns a fresh empty lease if the file
    /// is missing, empty, or malformed.</summary>
    public static TestBenchLeaseState ReadState()
    {
        var path = GetStateFilePath();
        try
        {
            if (!File.Exists(path)) return new TestBenchLeaseState();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new TestBenchLeaseState();
            return JsonSerializer.Deserialize<TestBenchLeaseState>(text, StateJsonOptions)
                   ?? new TestBenchLeaseState();
        }
        catch (JsonException)
        {
            // Malformed — treat as fresh. The reaper will overwrite the next time it acts.
            return new TestBenchLeaseState();
        }
    }

    /// <summary>Persists the lease state to <see cref="GetStateFilePath"/> via a temp-file
    /// rename so a torn write cannot corrupt the file readers observe.</summary>
    public static void WriteState(TestBenchLeaseState state)
    {
        var path = GetStateFilePath();
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(state, StateJsonOptions);
        File.WriteAllText(tmp, json);
        // File.Move overwrite is atomic within the same filesystem on both Linux and Windows.
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Deletes the state file. The lock file self-deletes when its handle closes.</summary>
    public static void DeleteState()
    {
        var path = GetStateFilePath();
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Removes entries in <paramref name="users"/> whose PID no longer resolves to
    /// a running process. Used to keep the "still-attached" list honest across crash/kill.</summary>
    public static List<TestBenchUser> PruneStale(IReadOnlyList<TestBenchUser> users)
    {
        var alive = new List<TestBenchUser>(users.Count);
        foreach (var user in users)
        {
            if (IsAlive(user)) alive.Add(user);
        }
        return alive;
    }

    /// <summary>Returns true when <paramref name="user"/>'s recorded PID still resolves to a
    /// running process whose creation time matches <see cref="TestBenchUser.PidCreatedUtc"/>.
    /// The creation-time cross-check guards against PID reuse between test runs (Linux PID
    /// wraparound is short in dense CI environments).</summary>
    public static bool IsAlive(TestBenchUser user)
    {
        try
        {
            using var proc = Process.GetProcessById(user.Pid);
            // StartTime accessor throws on some kernel-thread edge cases; wrap defensively.
            var started = proc.StartTime.ToUniversalTime();
            // Allow 500ms of slop for filesystem timestamp rounding.
            return Math.Abs((started - user.PidCreatedUtc).TotalSeconds) < 1.0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Snapshot of this process's identity for the users list.</summary>
    public static TestBenchUser CurrentUser(string? testHostName = null)
    {
        using var current = Process.GetCurrentProcess();
        return new TestBenchUser(
            Pid: current.Id,
            PidCreatedUtc: current.StartTime.ToUniversalTime(),
            TestHost: testHostName ?? current.ProcessName);
    }

    private sealed class LockHandle : IDisposable
    {
        private FileStream? _stream;

        public LockHandle(FileStream stream) { _stream = stream; }

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _stream, null);
            if (s is null) return;
            try { s.Dispose(); }
            catch { /* best-effort */ }
        }
    }
}

/// <summary>Serialised lease payload. See <see cref="TestBenchLease"/>.</summary>
public sealed class TestBenchLeaseState
{
    public DateTime LastAttachUtc { get; set; }
    public List<TestBenchUser> Users { get; set; } = new();
    public BenchPorts Ports { get; set; } = new();
    /// <summary>Migration marker per engine (<c>postgres</c> / <c>scylla</c> / <c>cassandra</c>).
    /// Advisory only — the migrators themselves are idempotent, but skipping them on hot attach
    /// shaves seconds off every subsequent test-host boot.</summary>
    public Dictionary<string, bool> MigrationsApplied { get; set; } = new();
}

/// <summary>Fixed-port bindings for the bench containers. Ports are the host-side numbers
/// that <see cref="TestBenchCoordinator"/> both probes and passes to the AppHost so
/// discovery + attach stay in lockstep.</summary>
public sealed class BenchPorts
{
    public int Postgres { get; set; } = 14200;
    public int Scylla { get; set; } = 19042;
    public int Cassandra { get; set; } = 19043;
}

/// <summary>A single test-host process attached to the bench. PID + StartTime doubles as
/// a poor man's process identity so a reused PID cannot masquerade as an old attach.</summary>
public sealed record TestBenchUser(int Pid, DateTime PidCreatedUtc, string TestHost);
