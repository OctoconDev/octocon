using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// HttpMessageHandler that serves canned JSON responses for Simply Plural API URLs.
///
/// Tests register responses by either exact path (<see cref="OnGet(string, string)"/>)
/// or by path prefix (<see cref="OnGetPrefix(string, string)"/>), and assert against
/// <see cref="Requests"/> / <see cref="RequestsTo(string)"/> to validate request count
/// and ordering. Apparyllis is being sunset, so all SP coverage must be replay-based;
/// this handler is the single mock surface used by every SP import test.
///
/// Unmatched paths return 404 so a forgotten stub fails loudly rather than silently.
/// Exact matches take precedence over prefix matches.
/// </summary>
internal sealed class StubSpHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, string Body)> _prefixes = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly object _prefixLock = new();

    public StubSpHandler OnGet(string path, string responseBody)
    {
        _exact[path] = responseBody;
        return this;
    }

    public StubSpHandler OnGetPrefix(string pathPrefix, string responseBody)
    {
        lock (_prefixLock)
        {
            _prefixes.Add((pathPrefix, responseBody));
        }
        return this;
    }

    public IReadOnlyList<string> Requests => _requests.ToArray();

    public IReadOnlyList<string> RequestsTo(string pathPrefix)
        => _requests.Where(r => r.StartsWith(pathPrefix, StringComparison.Ordinal)).ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Strip query string for matching but keep it in the recorded URL so tests can
        // inspect e.g. ?startTime=...&endTime=... ranges issued by ImportFrontsAsync.
        var uri = request.RequestUri ?? throw new InvalidOperationException("StubSpHandler received a request without a URI.");
        var pathAndQuery = uri.PathAndQuery;
        var pathOnly = uri.AbsolutePath;
        _requests.Enqueue(pathAndQuery);

        if (_exact.TryGetValue(pathOnly, out var body))
        {
            return Task.FromResult(Json(body));
        }

        string? prefixBody = null;
        lock (_prefixLock)
        {
            foreach (var (prefix, p) in _prefixes)
            {
                if (pathOnly.StartsWith(prefix, StringComparison.Ordinal))
                {
                    prefixBody = p;
                    break;
                }
            }
        }

        if (prefixBody is not null)
        {
            return Task.FromResult(Json(prefixBody));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No StubSpHandler stub registered for {pathOnly}", Encoding.UTF8, "text/plain"),
        });
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}
