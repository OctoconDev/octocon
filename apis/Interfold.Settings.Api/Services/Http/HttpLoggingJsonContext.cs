using System.Text.Json.Serialization;

namespace Interfold.Api.Services.Http;

[JsonSerializable(typeof(RequestMeta))]
[JsonSerializable(typeof(ResponseMeta))]
internal partial class HttpLoggingJsonContext : JsonSerializerContext;

