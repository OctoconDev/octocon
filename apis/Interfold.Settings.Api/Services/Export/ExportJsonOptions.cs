using System.Text.Json;

namespace Interfold.Api.Services.Export;

// Every wire key is stamped by [JsonPropertyName] on the payload records, so no naming
// policy is applied — a missing/typo'd key breaks the pinned-fixture test loudly rather
// than being silently rewritten by SnakeCaseLower. `DefaultIgnoreCondition` is left at
// the default so the reference's null fields (color/description/parent_tag_id/…) survive
// on the wire.
public static class ExportJsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
    };
}
