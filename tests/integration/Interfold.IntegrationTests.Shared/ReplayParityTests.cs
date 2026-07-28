using System.Text;
using System.Text.Json;
using Interfold.IntegrationTests.Shared.Models;
using Interfold.IntegrationTests.Shared.TestServices;

namespace Interfold.IntegrationTests.Shared;

/// <summary>
/// Deterministic replay parity tests (Phase N, Scope 1).
/// <para>
/// Each <c>*.trace.json</c> fixture under <c>Fixtures/</c> is executed against a live
/// in-memory API process.  Every step asserts HTTP status and (where declared) the
/// <c>replay</c> flag in the response body.
/// </para>
/// Gated on <c>OCTOCON_RUN_API_INTEGRATION=true</c>.
/// </summary>
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class ReplayParityTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    public static IEnumerable<string> GetReplayFiles()
    {
        yield return "alter-lifecycle.trace.json";
        yield return "tag-lifecycle.trace.json";
        yield return "fronting-lifecycle.trace.json";
        yield return "fronting-delete-parity.trace.json";
        yield return "poll-lifecycle.trace.json";
        yield return "settings-lifecycle.trace.json";
        yield return "journal-lifecycle.trace.json";
        yield return "friendship-lifecycle.trace.json";
        yield return "fronting-visibility.trace.json";
    }
    
    [Test]
    [CombinedDataSources]
    public async Task Replay_PassesAllSteps([MethodDataSource(typeof(ReplayParityTests), nameof(GetReplayFiles))] string fixtureFileName)
    {
        await RunTraceAsync(fixture.Factory, fixtureFileName);
    }

    // -----------------------------------------------------------------------
    // Core runner
    // -----------------------------------------------------------------------

    private static async Task RunTraceAsync(InterfoldWebApplicationFactory factory,  string fixtureFileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);

        var trace = ReplayTrace.Load(fixturePath);
        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(fixturePath)).IsTrue();
            await Assert.That(trace.Steps.Count > 0).IsTrue();
        }

        using var client = TestClient.NoRedirect(factory);

        // Build a per-invocation identity namespace so the same trace fixture can run
        // independently across the InMemory / Scylla / Cassandra factory variants AND
        // across concurrent leaf integration projects. PostgresIdempotencyStore is
        // registered for every db-backed run via PostgresServiceCollectionExtensions,
        // and under the centralised test-bench (see TestBenchCoordinator) that store's
        // Postgres instance is shared across every project. Without a per-invocation
        // discriminator, the second consumer of any given trace — variant or project —
        // would replay the first's outcome and receive an AlterId/EntryId that its own
        // CQL backend never wrote, making the controller's read-back-after-create
        // return null and surface as `unknown_error` 500. The 8-hex nonce is bounded
        // so the composed principal stays inside SettingsUsernameRequest's validator.
        var principalSuffix = $"{SanitizeIdentitySuffix(factory.DisplayName)}-{Guid.NewGuid().ToString("N")[..8]}";
        var principalMap = trace.Steps
            .Select(s => s.PrincipalId)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, p => $"{p}-{principalSuffix}", StringComparer.OrdinalIgnoreCase);

        // Seed all principals referenced in the trace so Scylla/Cassandra
        // backends have the user rows before operations execute.
        foreach (var principal in principalMap.Values)
        {
            await EnsureUserExistsAsync(client, principal);
        }

        // Mutable variable store for {varName} substitutions across steps.
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in trace.Steps)
        {
            await ExecuteStepAsync(client, step, vars, principalMap, principalSuffix, fixtureFileName);
        }
    }

    /// <summary>
    /// Lowercases the factory's DisplayName and replaces anything outside [a-z0-9-] with '-' so the
    /// suffix stays acceptable as a username (POST /api/settings/username has its own validation
    /// rules) and stays embeddable in URL path segments without further escaping.
    /// </summary>
    private static string SanitizeIdentitySuffix(string displayName)
    {
        var lowered = displayName.ToLowerInvariant();
        var buffer = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
            buffer.Append(char.IsAsciiLetterOrDigit(ch) || ch == '-' ? ch : '-');
        var result = buffer.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? "fixture" : result;
    }

    private static async Task ExecuteStepAsync(
        HttpClient client,
        ReplayStep step,
        Dictionary<string, string> vars,
        IReadOnlyDictionary<string, string> principalMap,
        string principalSuffix,
        string fixtureFileName)
    {
        var path = ApplyPrincipalRewrites(SubstituteVars(step.Path, vars), principalMap);

        using var request = new HttpRequestMessage(
            new HttpMethod(step.Method.ToUpperInvariant()), path);


        if (!string.IsNullOrWhiteSpace(step.IdempotencyKey))
        {
            // Suffix idempotency keys with the same fixture-namespace tag the principal IDs
            // get. The Postgres idempotency store keys on (PrincipalId, OperationId,
            // IdempotencyKey); the principal already carries the suffix here, but keying the
            // idempotency value too keeps logs/diagnostics aligned and protects against any
            // future store implementation that ignores PrincipalId in its uniqueness key.
            request.Headers.Add("X-Interfold-Idempotency-Key", $"{step.IdempotencyKey}-{principalSuffix}");
        }

        if (step.Body.HasValue)
        {
            var substitutedBody = SubstituteBodyValue(step.Body.Value, vars, principalMap);
            var bodyJson = JsonSerializer.Serialize(substitutedBody);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrWhiteSpace(step.PrincipalId)
            && principalMap.TryGetValue(step.PrincipalId, out var rewrittenPrincipal))
        {
            AttachPrincipalAuth(request, client, rewrittenPrincipal);
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
        await Assert.That((int)response.StatusCode).IsEqualTo(step.ExpectedStatus)
            .Because($"[{fixtureFileName}] Step '{step.Name}' ({step.Method.ToUpperInvariant()} {path}) expected {step.ExpectedStatus}, got {(int)response.StatusCode}. Request body: {requestBody}. Response body: {body}");

        // Assert replay flag when declared (only when the response has a body).
        // Traces drive arbitrary endpoints so we still parse the raw envelope here rather
        // than deserialise to any single typed contract — the trace format is
        // wire-shape-first by design.
        if (step.ExpectedReplay.HasValue && !string.IsNullOrEmpty(body))
        {
            using var replayDoc = JsonDocument.Parse(body);
            var actualReplay =
                replayDoc.RootElement.ValueKind == JsonValueKind.Object
                && replayDoc.RootElement.TryGetProperty("replay", out var replayProp)
                && (replayProp.ValueKind == JsonValueKind.True
                    || replayProp.ValueKind == JsonValueKind.False)
                && replayProp.GetBoolean();

            await Assert.That(actualReplay == step.ExpectedReplay.Value).IsTrue();
        }

        if (!string.IsNullOrEmpty(step.ExpectedJsonContains))
        {
            await Assert.That(body).Contains(step.ExpectedJsonContains)
                .Because($"[{fixtureFileName}] Step '{step.Name}': expected body to contain '{step.ExpectedJsonContains}'");
        }

        if (!string.IsNullOrEmpty(step.ExpectedJsonNotContains))
        {
            await Assert.That(body).DoesNotContain(step.ExpectedJsonNotContains)
                .Because($"[{fixtureFileName}] Step '{step.Name}': expected body to NOT contain '{step.ExpectedJsonNotContains}'");
        }

        // Capture fields for subsequent steps.
        if (step.CaptureAs is { Count: > 0 } captures && response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var (varName, jsonPath) in captures)
            {
                if (TryCaptureValue(doc.RootElement, jsonPath, out var capturedValue))
                {
                    vars[varName] = capturedValue;
                }
                else
                {
                    Assert.Fail($"[{fixtureFileName}] Step '{step.Name}': could not capture '{jsonPath}' from body: {body}");
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // JSON helpers
    // -----------------------------------------------------------------------

    private static string SubstituteVars(string template, Dictionary<string, string> vars)
    {
        foreach (var (key, value) in vars)
            template = template.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);

        return template;
    }

    /// <summary>
    /// Rewrites every raw principal id present in <paramref name="text"/> with its suffixed
    /// counterpart from <paramref name="principalMap"/>. Used for path segments like
    /// <c>/api/friend-requests/replay-sys-friend-b</c> where the path identifies a *different*
    /// principal than the one issuing the request — that target principal's id needs the same
    /// fixture suffix that <see cref="RunTraceAsync"/> applied to the issuing principal,
    /// otherwise the API would resolve a non-existent user and the trace would diverge.
    /// </summary>
    private static string ApplyPrincipalRewrites(string text, IReadOnlyDictionary<string, string> principalMap)
    {
        // Rewrite longest principal ids first so that prefix-overlapping ids ("replay-sys-friend"
        // vs "replay-sys-friend-b") never get partially rewritten. The trace fixtures don't
        // currently rely on this, but the discipline keeps future fixtures safe.
        foreach (var (raw, rewritten) in principalMap.OrderByDescending(p => p.Key.Length))
            text = text.Replace(raw, rewritten, StringComparison.Ordinal);

        return text;
    }

    private static object? SubstituteBodyValue(
        JsonElement element,
        Dictionary<string, string> vars,
        IReadOnlyDictionary<string, string> principalMap)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = SubstituteBodyValue(property.Value, vars, principalMap);
                }

                return dict;
            }

            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(SubstituteBodyValue(item, vars, principalMap));
                }

                return list;
            }

            case JsonValueKind.String:
            {
                var value = element.GetString() ?? string.Empty;
                if (TryResolvePlaceholderValue(value, vars, out var resolved))
                    return resolved;

                return ApplyPrincipalRewrites(SubstituteVars(value, vars), principalMap);
            }

            case JsonValueKind.Number:
                if (element.TryGetInt32(out var intValue)) return intValue;
                if (element.TryGetInt64(out var longValue)) return longValue;
                if (element.TryGetDecimal(out var decimalValue)) return decimalValue;
                return element.GetDouble();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            default:
                return element.GetRawText();
        }
    }

    private static bool TryResolvePlaceholderValue(
        string value,
        Dictionary<string, string> vars,
        out object? resolved)
    {
        resolved = null;

        if (value.Length < 3 || value[0] != '{' || value[^1] != '}')
            return false;

        var variableName = value[1..^1];
        if (!vars.TryGetValue(variableName, out var variableValue))
            return false;

        if (int.TryParse(variableValue, out var intValue))
        {
            resolved = intValue;
            return true;
        }

        if (long.TryParse(variableValue, out var longValue))
        {
            resolved = longValue;
            return true;
        }

        if (decimal.TryParse(variableValue, out var decimalValue))
        {
            resolved = decimalValue;
            return true;
        }

        if (bool.TryParse(variableValue, out var boolValue))
        {
            resolved = boolValue;
            return true;
        }

        resolved = variableValue;
        return true;
    }

    private static bool TryCaptureValue(JsonElement root, string jsonPath, out string value)
    {
        foreach (var candidate in GetCaptureCandidates(jsonPath))
        {
            if (TryReadStringField(root, candidate, out value))
                return true;

            if (TryReadIntField(root, candidate, out var intValue))
            {
                value = intValue.ToString();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetCaptureCandidates(string jsonPath)
    {
        yield return jsonPath;

        if (jsonPath.Equals("frontId", StringComparison.OrdinalIgnoreCase))
        {
            yield return "front_id";
        }

        if (jsonPath.Equals("alterId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("entryId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("pollId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("tagId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("frontId", StringComparison.OrdinalIgnoreCase))
        {
            // Backward compatibility for fixtures that still request legacy create keys.
            yield return "id";
        }
    }

    private static bool TryReadStringField(JsonElement root, string name, out string value)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                value = prop.Value.GetString()!;
                return true;
            }
        }

        // Also search inside a top-level "data" object (201 create responses use {data:{...}, replay:false}).
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in prop.Value.EnumerateObject())
                {
                    if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && child.Value.ValueKind == JsonValueKind.String)
                    {
                        value = child.Value.GetString()!;
                        return true;
                    }
                }
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadIntField(JsonElement root, string name, out int value)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt32(out value))
            {
                return true;
            }
        }

        // Also search inside a top-level "data" object.
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in prop.Value.EnumerateObject())
                {
                    if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && child.Value.ValueKind == JsonValueKind.Number
                        && child.Value.TryGetInt32(out value))
                    {
                        return true;
                    }
                }
            }
        }

        value = 0;
        return false;
    }
}
