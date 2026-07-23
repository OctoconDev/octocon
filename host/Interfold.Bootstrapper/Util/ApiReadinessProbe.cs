using System.Net;
using Interfold.Bootstrapper.Cli;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Shared HTTP readiness probe for the API's <c>/health/ready</c> endpoint. Used by
/// <see cref="Phases.LaunchPhase"/> (post-launch health gate) and
/// <see cref="Phases.UpdateImagesPhase"/> (post-recreate health check) — both previously
/// carried near-identical polling loops.
/// </summary>
internal static class ApiReadinessProbe
{
    /// <summary>
    /// Polls <c>http://localhost:{apiHttpPort}/health/ready</c> until it returns 200 or
    /// <paramref name="deadline"/> is reached. Returns <c>null</c> on success, or a short
    /// operator-facing description on timeout.
    /// </summary>
    public static async Task<string?> TryWaitUntilAsync(
        int apiHttpPort, DateTime deadline, PhaseLogger logger, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"http://localhost:{apiHttpPort}{HealthEndpoints.Ready}";
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    logger.Info($"    api ready at {url} after {attempt} attempt(s)");
                    return null;
                }
            }
            catch (HttpRequestException) { /* container may not be listening yet */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* per-request timeout */ }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return $"api did not return 200 at {url} within the health-check budget";
    }
}
