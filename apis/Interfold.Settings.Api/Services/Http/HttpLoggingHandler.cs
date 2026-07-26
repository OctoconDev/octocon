using System.Text.Json;

namespace Interfold.Settings.Api.Services.Http;

public class HttpLoggingHandler : DelegatingHandler
{
    private readonly string _basePath;
    private readonly ILogger<HttpLoggingHandler> _logger;
    private readonly bool _replayEnabled;
    private readonly bool _replayStrict;
    private readonly bool _enabled;

    public HttpLoggingHandler(IConfiguration configuration, ILogger<HttpLoggingHandler> logger)
    {
        _logger = logger;
        _basePath = configuration["HttpMessageLog:BasePath"] ?? "http-messages";
        _replayEnabled = configuration.GetValue("HttpMessageLog:Replay:Enabled", false);
        _replayStrict = configuration.GetValue("HttpMessageLog:Replay:Strict", false);
        _enabled = configuration.GetValue("HttpMessageLog:Enabled", false);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var uri = request.RequestUri ?? new Uri("http://unknown/");

            var segments = uri.Segments
                .Select(s => s.Trim('/'))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            var host = uri.Host ?? "unknown";

            // Layout: <BasePath>/<host>/<path-segments>/<request|response>/
            var endpoint = segments.Length > 0 ? segments.Last() : "root";
            var folderSegments = new List<string> { _basePath, host };
            if (segments.Length > 1)
                folderSegments.AddRange(segments.Take(segments.Length - 1));

            var requestFolder = Path.Combine(folderSegments.Concat(new[] { "request" }).ToArray());
            Directory.CreateDirectory(requestFolder);

            if (request.Content != null)
            {
                var reqBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                var reqExt = GetExtensionFromMediaType(request.Content.Headers.ContentType?.MediaType);
                var reqFile = Path.Combine(requestFolder, $"{SanitizeFileName(endpoint)}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{requestId}.request{reqExt}");
                await File.WriteAllBytesAsync(reqFile, reqBytes, cancellationToken).ConfigureAwait(false);

                // Save metadata
                var reqMeta = new RequestMeta
                {
                    Method = request.Method.Method,
                    Uri = uri.ToString(),
                    RequestHost = uri.Host,
                    NormalizedRequestPathQuery = NormalizeUriForMatching(uri.ToString()),
                    Headers = request.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray()),
                    ContentHeaders = request.Content.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray())
                };
                var reqMetaFile = Path.ChangeExtension(reqFile, ".meta.json");
                await File.WriteAllTextAsync(reqMetaFile, JsonSerializer.Serialize(reqMeta, HttpLoggingJsonContext.Default.RequestMeta), cancellationToken).ConfigureAwait(false);

                // We already consumed the request body; hand back a fresh copy.
                var newContent = new ByteArrayContent(reqBytes);
                CopyHttpContentHeaders(request.Content, newContent);
                request.Content = newContent;
            }
            else
            {
                var reqFile = Path.Combine(requestFolder, $"{SanitizeFileName(endpoint)}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{requestId}.request");
                var reqMeta = new RequestMeta
                {
                    Method = request.Method.Method,
                    Uri = uri.ToString(),
                    RequestHost = uri.Host,
                    NormalizedRequestPathQuery = NormalizeUriForMatching(uri.ToString()),
                    Headers = request.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray()),
                    ContentHeaders = null
                };
                var reqMetaFile = Path.ChangeExtension(reqFile, ".meta.json");
                Directory.CreateDirectory(Path.GetDirectoryName(reqMetaFile) ?? requestFolder);
                await File.WriteAllTextAsync(reqMetaFile, JsonSerializer.Serialize(reqMeta, HttpLoggingJsonContext.Default.RequestMeta), cancellationToken).ConfigureAwait(false);
            }

            var response = default(HttpResponseMessage?);
            if (_replayEnabled)
            {
                try
                {
                    var replay = TryGetReplayResponse(folderSegments, endpoint, request.Method.Method, uri.ToString(), uri.Host);
                    if (replay != null)
                    {
                        _logger.LogInformation("Replaying HTTP response from disk for {Uri}", uri);
                        response = replay;
                    }
                    else if (_replayStrict)
                    {
                        throw new InvalidOperationException($"Replay enabled but no recorded response found for {uri}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load replay response");
                    if (_replayStrict)
                        throw;
                }
            }

            response ??= await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var responseFolder = Path.Combine(folderSegments.Concat(new[] { "response" }).ToArray());
            Directory.CreateDirectory(responseFolder);

            if (response.Content != null)
            {
                var respBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var respExt = GetExtensionFromMediaType(response.Content.Headers.ContentType?.MediaType);
                var respFile = Path.Combine(responseFolder, $"{SanitizeFileName(endpoint)}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{requestId}.response{respExt}");
                await File.WriteAllBytesAsync(respFile, respBytes, cancellationToken).ConfigureAwait(false);

                var respMeta = new ResponseMeta
                {
                    RequestMethod = request.Method.Method,
                    RequestUri = uri.ToString(),
                    RequestHost = uri.Host,
                    NormalizedRequestPathQuery = NormalizeUriForMatching(uri.ToString()),
                    Status = (int)response.StatusCode,
                    Reason = response.ReasonPhrase,
                    Uri = uri.ToString(),
                    Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray()),
                    ContentHeaders = response.Content.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray())
                };
                var respMetaFile = Path.ChangeExtension(respFile, ".meta.json");
                await File.WriteAllTextAsync(respMetaFile, JsonSerializer.Serialize(respMeta, HttpLoggingJsonContext.Default.ResponseMeta), cancellationToken).ConfigureAwait(false);

                var newRespContent = new ByteArrayContent(respBytes);
                CopyHttpContentHeaders(response.Content, newRespContent);
                response.Content = newRespContent;
            }
            else
            {
                var respFile = Path.Combine(responseFolder, $"{SanitizeFileName(endpoint)}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{requestId}.response");
                var respMeta = new ResponseMeta
                {
                    RequestMethod = request.Method.Method,
                    RequestUri = uri.ToString(),
                    RequestHost = uri.Host,
                    NormalizedRequestPathQuery = NormalizeUriForMatching(uri.ToString()),
                    Status = (int)response.StatusCode,
                    Reason = response.ReasonPhrase,
                    Uri = uri.ToString(),
                    Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray()),
                    ContentHeaders = null
                };
                var respMetaFile = Path.ChangeExtension(respFile, ".meta.json");
                Directory.CreateDirectory(Path.GetDirectoryName(respMetaFile) ?? responseFolder);
                await File.WriteAllTextAsync(respMetaFile, JsonSerializer.Serialize(respMeta, HttpLoggingJsonContext.Default.ResponseMeta), cancellationToken).ConfigureAwait(false);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log HTTP request/response");
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void CopyHttpContentHeaders(HttpContent? source, HttpContent target)
    {
        if (source == null) return;
        foreach (var header in source.Headers)
        {
            if (!target.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                try
                {
                    target.Headers.Add(header.Key, header.Value);
                }
                catch
                {
                }
            }
        }
    }

    private static string GetExtensionFromMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return ".bin";

        var type = mediaType.Split(';')[0].Trim().ToLowerInvariant();
        return type switch
        {
            "application/json" or "text/json" => ".json",
            "text/plain" => ".txt",
            "text/html" => ".html",
            "application/xml" or "text/xml" => ".xml",

            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",

            _ => ".bin"
        };
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // Returns the newest recorded response whose method/normalized-URI matches, or null.
    private HttpResponseMessage? TryGetReplayResponse(List<string> folderSegments, string endpoint, string requestMethod, string requestUri, string requestHost)
    {
        try
        {
            var responseFolder = Path.Combine(folderSegments.Concat(new[] { "response" }).ToArray());
            if (!Directory.Exists(responseFolder))
                return null;

            var files = Directory.EnumerateFiles(responseFolder)
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Name.Contains(".response"))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToArray();

            if (files.Length == 0)
                return null;

            FileInfo? chosen = null;
            JsonElement? chosenMetaRoot = null;

            foreach (var fi in files)
            {
                var metaFile = Path.ChangeExtension(fi.FullName, ".meta.json");
                if (!File.Exists(metaFile))
                    continue;

                try
                {
                    var metaJson = File.ReadAllText(metaFile);
                    var recorded = JsonSerializer.Deserialize<ResponseMeta>(metaJson, HttpLoggingJsonContext.Default.ResponseMeta);
                    if (recorded == null)
                        continue;

                    var rm = recorded.RequestMethod;
                    var ru = recorded.RequestUri;
                    var rh = recorded.RequestHost;

                    var normalizedRecorded = ru != null ? NormalizeUriForMatching(ru) : null;
                    var normalizedCurrent = NormalizeUriForMatching(requestUri);

                    var recordedHostToCompare = rh;
                    if (string.IsNullOrEmpty(recordedHostToCompare) && ru != null)
                    {
                        if (Uri.TryCreate(ru, UriKind.Absolute, out var tmp))
                            recordedHostToCompare = tmp.Host;
                    }

                    if (!string.IsNullOrEmpty(recordedHostToCompare) && !string.Equals(recordedHostToCompare, requestHost, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(rm, requestMethod, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(normalizedRecorded, normalizedCurrent, StringComparison.Ordinal))
                    {
                        chosen = fi;
                        chosenMetaRoot = JsonDocument.Parse(metaJson).RootElement.Clone();
                        break;
                    }
                }
                catch
                {
                }
            }

            if (chosen == null || chosenMetaRoot == null)
                return null;

            var metaJsonText = File.ReadAllText(Path.ChangeExtension(chosen.FullName, ".meta.json"));
            var recordedMeta = JsonSerializer.Deserialize<ResponseMeta>(metaJsonText, HttpLoggingJsonContext.Default.ResponseMeta);

            var status = recordedMeta?.Status ?? 200;
            var reason = recordedMeta?.Reason ?? string.Empty;

            var contentBytes = File.ReadAllBytes(chosen.FullName);
            var content = new ByteArrayContent(contentBytes);

            if (recordedMeta?.ContentHeaders is not null)
            {
                foreach (var kv in recordedMeta.ContentHeaders)
                {
                    try
                    {
                        foreach (var v in kv.Value ?? Array.Empty<string>())
                            content.Headers.TryAddWithoutValidation(kv.Key, v);
                    }
                    catch
                    {
                    }
                }
            }

            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                ReasonPhrase = reason,
                Content = content
            };

            if (recordedMeta?.Headers is not null)
            {
                foreach (var kv in recordedMeta.Headers)
                {
                    try
                    {
                        foreach (var v in kv.Value ?? Array.Empty<string>())
                            response.Headers.TryAddWithoutValidation(kv.Key, v);
                    }
                    catch
                    {
                    }
                }
            }

            return response;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeUriForMatching(string uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString))
            return uriString ?? string.Empty;

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return uriString;

        // Match by path + query only — recorded responses are host-agnostic.
        var path = uri.AbsolutePath;
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length <= 1)
            return path;

        var qs = query.TrimStart('?');
        var parts = qs.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<(string Name, string Value)>();
        foreach (var p in parts)
        {
            var idx = p.IndexOf('=');
            if (idx >= 0)
            {
                var n = Uri.UnescapeDataString(p.Substring(0, idx));
                var v = Uri.UnescapeDataString(p.Substring(idx + 1));
                list.Add((n, v));
            }
            else
            {
                var n = Uri.UnescapeDataString(p);
                list.Add((n, string.Empty));
            }
        }

        var ordered = list.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Value, StringComparer.Ordinal);
        var rebuilt = string.Join("&", ordered.Select(t =>
        {
            if (string.IsNullOrEmpty(t.Value))
                return Uri.EscapeDataString(t.Name);
            return Uri.EscapeDataString(t.Name) + "=" + Uri.EscapeDataString(t.Value);
        }));

        return path + "?" + rebuilt;
    }
}