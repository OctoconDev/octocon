using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Api.Models;
using Interfold.Contracts;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Lightweight test-side mirror of <see cref="SuccessResponse{T}"/> — the API's own type
/// carries a <c>[JsonIgnore]</c> ctor parameter (<c>StatusCode</c>) that STJ refuses to
/// deserialise ("Each parameter in the deserialization constructor ... must bind to an
/// object property or field"). We deserialise into this shape and callers get the exact
/// same <c>Data</c> / <c>Replay</c> semantics without needing to touch the production
/// envelope. If <see cref="SuccessResponse{T}"/> ever grows a public parameterless ctor
/// this type can collapse back onto it.
/// </summary>
internal sealed record TestEnvelope<T>(
    T Data,
    [property: JsonPropertyName("replay")] bool? Replay = null
);

/// <summary>
/// Test-side mirror of <see cref="ErrorResponse"/>. Same reason as <see cref="TestEnvelope{T}"/>:
/// the production type has a <c>[JsonIgnore]</c> ctor parameter (<c>statusCode</c>) that STJ
/// refuses to bind on deserialisation. Field names / types are byte-identical to the wire
/// contract so <c>error.Code</c>, <c>error.Error</c>, <c>error.EntityRef</c>, and
/// <c>error.Detail</c> read the same at call sites regardless of which shape is used.
/// </summary>
internal sealed record TestErrorResponse(
    string Error,
    ErrorCode Code,
    [property: JsonPropertyName("entity_ref")] string? EntityRef = null,
    string? Detail = null
);

/// <summary>
/// Shared JSON plumbing for the integration test suite. The whole test project routes every
/// serialise / deserialise through <see cref="Options"/> so the body bytes on the wire are
/// byte-identical to what the API's own <c>PropertyNamingPolicy = SnakeCaseLower</c>
/// serialiser produces — the idempotency-key hash the server takes over the JSON payload
/// depends on that canonical shape, so any drift here would silently break the replay tests.
/// The <see cref="SendAsJsonAsync{TReq}"/> and <see cref="ReadEnvelopeAsync{T}"/> extensions
/// are the only helpers tests should reach for; the built-in
/// <see cref="System.Net.Http.Json.HttpClientJsonExtensions"/> methods don't expose the
/// <c>HttpRequestMessage</c> hook we need to attach principal-auth headers before sending.
/// </summary>
internal static class TestJson
{
    /// <summary>
    /// Mirrors the API's <c>AddJsonOptions(options =&gt; options.JsonSerializerOptions
    /// .PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)</c> in Program.cs. Cached so
    /// every call site pays the reflection/warm-up cost once, and so the idempotency-hash
    /// server-side sees the exact same canonical bytes on every send.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Sends a JSON body under the specified principal's auth. Optionally sets an
    /// <c>X-Interfold-Idempotency-Key</c> header (any non-null / non-empty value is added).
    /// This is the wrapper the built-in <c>HttpClient.PostAsJsonAsync</c> would give us if
    /// it exposed a <c>HttpRequestMessage</c> configure hook — the auth attachment mutates
    /// per-request headers, so we can't use the built-in overloads directly. The body is
    /// serialised through <see cref="Options"/> (snake_case).
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsJsonAsync<TReq>(
        this HttpClient client,
        HttpMethod method,
        string path,
        TReq body,
        string principal,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Options),
        };

        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a GET request under the specified principal's auth.
    /// This is the wrapper the built-in <c>HttpClient.GetAsync</c> would give us if
    /// it exposed a <c>HttpRequestMessage</c> configure hook — the auth attachment mutates
    /// per-request headers, so we can't use the built-in overloads directly.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAuthedGetAsync(
        this HttpClient client,
        string path,
        string principal,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an authenticated DELETE request under the specified principal's auth.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAuthedDeleteAsync(
        this HttpClient client,
        string path,
        string principal,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a JSON body without any auth attachment. Rare — mostly for negative tests that
    /// need to prove an anonymous request 401s. Routes through <see cref="Options"/> for
    /// snake_case consistency.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsJsonAnonymousAsync<TReq>(
        this HttpClient client,
        HttpMethod method,
        string path,
        TReq body,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Options),
        };

        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asserts the response status matches <paramref name="expected"/>, then deserialises
    /// the body into <see cref="SuccessResponse{T}"/>. The API always wraps successful
    /// payloads in <c>{ "data": ..., "replay": ... }</c>; this helper unwraps that envelope
    /// so tests can read <c>env.Data</c> and <c>env.Replay</c> directly.
    /// </summary>
    public static async Task<TestEnvelope<T>> ReadEnvelopeAsync<T>(
        this HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        await Assert.That(response.StatusCode)
            .IsEqualTo(expected)
            .Because($"Expected {(int)expected} {expected} from {response.RequestMessage?.Method} " +
                     $"{response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}. Body: {body}");

        var envelope = JsonSerializer.Deserialize<TestEnvelope<T>>(body, Options);
        if (envelope is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialise TestEnvelope<{typeof(T).Name}> from body: {body}");
        }

        return envelope;
    }

    /// <summary>
    /// Asserts the response status is <paramref name="expected"/> and returns the parsed
    /// error body. Used by tests that assert on <c>ErrorResponse.Code</c> or
    /// <c>ErrorResponse.EntityRef</c> instead of substring-matching the raw JSON.
    /// </summary>
    public static async Task<TestErrorResponse> ReadErrorAsync(
        this HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        await Assert.That(response.StatusCode)
            .IsEqualTo(expected)
            .Because($"Expected {(int)expected} {expected} from {response.RequestMessage?.Method} " +
                     $"{response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}. Body: {body}");

        var error = JsonSerializer.Deserialize<TestErrorResponse>(body, Options);
        if (error is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialise TestErrorResponse from body: {body}");
        }

        return error;
    }

    /// <summary>
    /// Deserialises the response body without asserting the status. For call sites that
    /// have already verified the status and just need the typed payload (e.g. reading a
    /// list-endpoint result inside a helper that itself asserts status).
    /// </summary>
    public static async Task<TestEnvelope<T>> ReadEnvelopeUncheckedAsync<T>(
        this HttpResponseMessage response,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TestEnvelope<T>>(body, Options)
               ?? throw new InvalidOperationException(
                   $"Failed to deserialise TestEnvelope<{typeof(T).Name}> from body: {body}");
    }

    /// <summary>
    /// Asserts the response status and deserialises the raw body into <typeparamref name="T"/>
    /// without unwrapping any envelope. Used by endpoints whose controller returns the payload
    /// directly (e.g. <c>NodeRoleController.GetNodeRole</c> returns
    /// <c>Ok(new NodeRoleResponse(...))</c> instead of a <c>SuccessResponse&lt;T&gt;</c>).
    /// </summary>
    public static async Task<T> ReadJsonAsync<T>(
        this HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        await Assert.That(response.StatusCode)
            .IsEqualTo(expected)
            .Because($"Expected {(int)expected} {expected} from {response.RequestMessage?.Method} " +
                     $"{response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}. Body: {body}");

        var value = JsonSerializer.Deserialize<T>(body, Options);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Failed to deserialise {typeof(T).Name} from body: {body}");
        }

        return value;
    }

    /// <summary>
    /// Escape hatch for tests that deliberately send malformed JSON (see
    /// <c>AlterIdValidationTests</c>' route-binding regression cases and the
    /// <c>type = "garbage"</c> settings-field enum-strictness test). Keeps the raw
    /// bytes explicit at the call site rather than hiding them behind a typed record.
    /// </summary>
    public static StringContent RawJsonContent(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// Sends raw JSON bytes (paired with <see cref="RawJsonContent"/>) under the specified
    /// principal's auth. Only used by tests that need bytes the typed request records
    /// can't produce — everything else must go through <see cref="SendAsJsonAsync{TReq}"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> SendRawJsonAsync(
        this HttpClient client,
        HttpMethod method,
        string path,
        string json,
        string principal,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = RawJsonContent(json),
        };

        BaseEndpointTest.AttachPrincipalAuth(request, client, principal);

        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }
}
