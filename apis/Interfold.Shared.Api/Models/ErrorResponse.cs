using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;
using Interfold.Shared.Contracts;

namespace Interfold.Api.Models;

public class ErrorResponse
{
    [SetsRequiredMembers]
    public ErrorResponse(
        string error,
        ErrorCode code,
        HttpStatusCode? statusCode = null,
        string? entityRef = null,
        string? detail = null)
    {
        Error = error;
        Code = code;
        StatusCode = statusCode ?? HttpStatusCode.InternalServerError;
        EntityRef = entityRef;
        Detail = detail;
    }

    public required string Error { get; init; }
    public required ErrorCode Code { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string? EntityRef { get; init; }

    // Optional human-readable clarification of the error, e.g. "pass redirect_uri on
    // GET /auth/{provider}...". Absorbs the payload that used to live on the deleted
    // OAuthRedirectErrorResponse record so both shapes share one wire contract; omitted
    // from the JSON envelope when null so unrelated ErrorResponse call sites are unaffected.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string? Detail { get; init; }

    [JsonIgnore]
    internal HttpStatusCode StatusCode { get; init; }
}
