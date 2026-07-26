using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.IntegrationTests.Shared.Models;

/// <summary>
/// A replay trace is a JSON file containing a sequence of <see cref="ReplayStep"/>s.
/// The test runner executes each step against a live API and asserts the declared outcome.
/// Format version: 1.
/// </summary>
public sealed class ReplayTrace
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<ReplayStep> Steps { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ReplayTrace Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ReplayTrace>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize trace at {path}");
    }

    public static IEnumerable<string> DiscoverFixtures()
    {
        var fixtureDir = Path.Combine(
            AppContext.BaseDirectory, "Fixtures");

        if (!Directory.Exists(fixtureDir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(fixtureDir, "*.trace.json", SearchOption.TopDirectoryOnly))
            yield return file;
    }
}

/// <summary>
/// A single operation in the replay trace.
/// </summary>
public sealed class ReplayStep
{
    /// <summary>Human-readable step name used in test output.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>HTTP method, e.g. <c>POST</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = "POST";

    /// <summary>
    /// Request path.  Supports <c>{stepVar}</c> substitution from <see cref="CaptureAs"/>.
    /// Example: <c>/api/systems/me/alters/{alterId}</c>
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Principal identity sent
    /// </summary>
    [JsonPropertyName("principalId")]
    public string PrincipalId { get; init; } = string.Empty;

    /// <summary>Idempotency key. Null means generate a fresh key each run.</summary>
    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; init; }

    /// <summary>JSON body to send with the request.</summary>
    [JsonPropertyName("body")]
    public JsonElement? Body { get; init; }

    /// <summary>Expected HTTP status code.</summary>
    [JsonPropertyName("expectedStatus")]
    public int ExpectedStatus { get; init; } = 200;

    /// <summary>
    /// When non-null, asserts the <c>replay</c> field in the response body.
    /// </summary>
    [JsonPropertyName("expectedReplay")]
    public bool? ExpectedReplay { get; init; }

    /// <summary>
    /// When non-null, asserts that the response body contains this substring.
    /// </summary>
    [JsonPropertyName("expectedJsonContains")]
    public string? ExpectedJsonContains { get; init; }

    /// <summary>
    /// When non-null, asserts that the response body does not contain this substring.
    /// </summary>
    [JsonPropertyName("expectedJsonNotContains")]
    public string? ExpectedJsonNotContains { get; init; }

    /// <summary>
    /// When non-null, captures the named field from the JSON response into a step
    /// variable that can be referenced via <c>{varName}</c> in subsequent step paths.
    /// Example: <c>{ "alterId": "alterId" }</c>
    /// </summary>
    [JsonPropertyName("captureAs")]
    public Dictionary<string, string>? CaptureAs { get; init; }
}
