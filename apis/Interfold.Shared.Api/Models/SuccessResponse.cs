using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;

namespace Interfold.Api.Models;

public class SuccessResponse<TValue>
{
    [SetsRequiredMembers]
    public SuccessResponse(TValue data, HttpStatusCode statusCode = HttpStatusCode.OK, bool? replay = null)
    {
        Data = data;
        StatusCode = statusCode;
        Replay = replay;
    }

    public required TValue Data { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Replay { get; init; }

    [JsonIgnore]
    internal HttpStatusCode StatusCode { get; init; }
}
