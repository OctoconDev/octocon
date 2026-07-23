namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// TEMPORARY (planned for removal): logs lifecycle ordering of TUnit hooks and
/// <see cref="SharedDbFixture"/> initialization so we can pick the right hook for the
/// scheduled-set-based <see cref="RequiredFixtures"/> rebuild. Intentionally a tiny static
/// helper so the cleanup commit is a single delete + the matching hook removals in
/// <c>BaseEndpointTest</c> + the call site in <c>SharedDbFixture.BuildArgs</c>.
/// </summary>
/// <remarks>
/// Writes to <c>artifacts/lifecycle-probe.ndjson</c> (one line per call) instead of
/// <c>Console.WriteLine</c> because TUnit's stdout interception only correlates output that
/// runs inside a <see cref="TUnit.Core.TestContext"/> async flow; the static hooks we care
/// about (<c>[Before(TestDiscovery)]</c>, <c>[After(TestDiscovery)]</c>,
/// <c>[Before(TestSession)]</c>) and <see cref="SharedDbFixture.BuildArgs"/> all run before
/// any per-test context exists, so their stdout is silently dropped. Writing to a file
/// sidesteps the interception entirely and keeps the probe diagnostic regardless of which
/// hook fires it.
/// </remarks>
public static class LifecycleProbe
{
    private static readonly object FileLock = new();
    private static readonly string LogPath = ResolveLogPath();
    private static int _sequence;

    static LifecycleProbe()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Truncate per-process so each test run produces a clean log.
            File.WriteAllText(LogPath, string.Empty);
        }
        catch
        {
            // The probe must never break a test run if the log path isn't writable; the
            // diagnostic value is "best effort" by design.
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log("ProcessExit");
    }

    /// <summary>
    /// Returns a disposable scope that logs <paramref name="endLabel"/> with elapsed ms on dispose.
    /// </summary>
    public static TimedScope BeginTimed(string endLabel)
        => new(endLabel);

    /// <summary>
    /// Writes a single line to the probe log capturing hook ordering plus whether
    /// <c>TestSessionContext.Current</c> / <c>TestDiscoveryContext.Current</c> already expose
    /// a populated <c>AllTests</c> list at this point. Reads via reflection so this file
    /// doesn't take a hard compile-time dependency on TUnit.Core types that move between
    /// versions; null-safe on every read so a missing context shows up as a label rather than
    /// throwing the probe out.
    /// </summary>
    public static void Log(string label, long? elapsedMs = null)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var sessionCount = ReadAllTestsCount("TUnit.Core.TestSessionContext, TUnit.Core");
        var discoveryCount = ReadAllTestsCount("TUnit.Core.TestDiscoveryContext, TUnit.Core");
        var elapsed = elapsedMs is null ? string.Empty : $",\"elapsedMs\":{elapsedMs.Value}";
        var line = $"{{\"seq\":{seq},\"timestamp\":\"{DateTime.UtcNow:O}\",\"label\":\"{label}\"" +
                   elapsed +
                   $",\"sessionCount\":\"{sessionCount}\",\"discoveryCount\":\"{discoveryCount}\"}}";

        try
        {
            lock (FileLock)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Swallow IO errors so the probe never fails a test run.
        }
    }

    private static string ResolveLogPath()
    {
        // Walk up from AppContext.BaseDirectory to find the repo root (marked by Interfold.sln
        // or Interfold.slnx). Anchoring under <repoRoot>/artifacts lines up with the rest of
        // the suite's log conventions and keeps probe output discoverable from
        // `artifacts/lifecycle-probe.ndjson` regardless of which working directory the test
        // runner executes from.
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && !string.IsNullOrEmpty(current); i++)
        {
            if (File.Exists(Path.Combine(current, "Interfold.slnx")) ||
                File.Exists(Path.Combine(current, "Interfold.sln")))
            {
                return Path.Combine(current, "artifacts", "lifecycle-probe.ndjson");
            }

            current = Path.GetDirectoryName(current);
        }

        // Fall back to the binary's directory if we can't find the repo root - still better
        // than silently dropping the probe output entirely.
        return Path.Combine(AppContext.BaseDirectory, "lifecycle-probe.ndjson");
    }

    private static string ReadAllTestsCount(string assemblyQualifiedTypeName)
    {
        var contextType = Type.GetType(assemblyQualifiedTypeName);
        if (contextType is null)
            return "(type-missing)";

        var current = contextType
            .GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?
            .GetValue(null);
        if (current is null)
            return "null";

        var allTests = contextType
            .GetProperty("AllTests", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?
            .GetValue(current);
        if (allTests is null)
            return "(prop-missing)";

        if (allTests is System.Collections.ICollection collection)
            return collection.Count.ToString();

        var counter = 0;
        if (allTests is System.Collections.IEnumerable enumerable)
        {
            foreach (var _ in enumerable)
                counter++;
            return counter.ToString();
        }

        return "(non-enumerable)";
    }

    public readonly struct TimedScope : IDisposable
    {
        private readonly string _endLabel;
        private readonly long _startedTicks;

        public TimedScope(string endLabel)
        {
            _endLabel = endLabel;
            _startedTicks = Environment.TickCount64;
        }

        public void Dispose()
        {
            var elapsed = Environment.TickCount64 - _startedTicks;
            Log(_endLabel, elapsed);
        }
    }
}
