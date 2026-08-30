using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Preflight for Docker Desktop / daemon memory before spinning Scylla + Cassandra.
/// Parallel integration runs (shared test-bench + Infrastructure's opted-out Aspire host)
/// need roughly 8 GiB; below that Scylla typically enters FailedToStart and the suite hangs
/// or cascades.</summary>
public static class DockerMemoryPreflight
{
    /// <summary>Soft floor for a full parallel integration matrix (GiB).</summary>
    public const double RecommendedGiB = 8.0;

    /// <summary>Hard floor — below this we refuse to launch rather than burn a 10-minute timeout.</summary>
    public const double MinimumGiB = 5.5;

    private static readonly Regex TotalMemoryRegex = new(
        @"Total Memory:\s*(?<value>[\d.]+)\s*(?<unit>GiB|MiB|KiB|B)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads <c>docker info</c> total memory. Returns null when docker is unavailable
    /// or the field can't be parsed (caller should proceed — absence of docker is a different failure).</summary>
    public static async Task<double?> TryGetTotalMemoryGiBAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                ArgumentList = { "info" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0) return null;

            var match = TotalMemoryRegex.Match(stdout);
            if (!match.Success) return null;
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;

            return match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "gib" => value,
                "mib" => value / 1024.0,
                "kib" => value / (1024.0 * 1024.0),
                "b" => value / (1024.0 * 1024.0 * 1024.0),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Throws when Docker reports less than <see cref="MinimumGiB"/>; logs a warning
    /// when below <see cref="RecommendedGiB"/>.</summary>
    public static async Task EnsureAdequateAsync(CancellationToken ct)
    {
        var gib = await TryGetTotalMemoryGiBAsync(ct).ConfigureAwait(false);
        if (gib is null) return;

        if (gib.Value + 0.01 < MinimumGiB)
        {
            throw new InvalidOperationException(
                $"Docker reports only {gib.Value:0.##} GiB total memory; parallel Interfold " +
                $"integration tests need at least {MinimumGiB:0.#} GiB (recommend {RecommendedGiB:0.#}+ GiB). " +
                "Raise Docker Desktop → Settings → Resources → Memory, then re-run. " +
                "With too little RAM Scylla enters FailedToStart while Postgres/Cassandra come up.");
        }

        if (gib.Value + 0.01 < RecommendedGiB)
        {
            Console.WriteLine(
                $"[docker-memory] warning: Docker has {gib.Value:0.##} GiB; recommend {RecommendedGiB:0.#}+ GiB " +
                "when the shared test-bench and Infrastructure's Aspire host both start Scylla.");
        }
    }
}
