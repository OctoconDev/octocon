using System.Text.Json.Serialization;

namespace Interfold.Api.Services.Http;

internal sealed class RequestMeta
{
    [JsonPropertyName("Method")]
    public string? Method { get; set; }

    [JsonPropertyName("Uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("RequestHost")]
    public string? RequestHost { get; set; }

    [JsonPropertyName("NormalizedRequestPathQuery")]
    public string? NormalizedRequestPathQuery { get; set; }

    [JsonPropertyName("Headers")]
    public Dictionary<string, string[]?>? Headers { get; set; }

    [JsonPropertyName("ContentHeaders")]
    public Dictionary<string, string[]?>? ContentHeaders { get; set; }
}

internal sealed class ResponseMeta
{
    [JsonPropertyName("RequestMethod")]
    public string? RequestMethod { get; set; }

    [JsonPropertyName("RequestUri")]
    public string? RequestUri { get; set; }

    [JsonPropertyName("RequestHost")]
    public string? RequestHost { get; set; }

    [JsonPropertyName("NormalizedRequestPathQuery")]
    public string? NormalizedRequestPathQuery { get; set; }

    [JsonPropertyName("Status")]
    public int Status { get; set; }

    [JsonPropertyName("Reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("Uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("Headers")]
    public Dictionary<string, string[]?>? Headers { get; set; }

    [JsonPropertyName("ContentHeaders")]
    public Dictionary<string, string[]?>? ContentHeaders { get; set; }
}

