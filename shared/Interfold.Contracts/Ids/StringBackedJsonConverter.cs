using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

// Common JSON converter shape for every string-backed wrapper struct — raw string in,
// raw string out (redaction lives on each struct's own ToString override). Read is
// null-tolerant (null → empty). Concrete stubs are sealed and named individually because
// [JsonConverter(typeof(...))] requires a concrete class name. Public so per-feature
// Contracts projects can derive their own concrete stubs.
public abstract class StringBackedJsonConverter<T> : JsonConverter<T>
{
    protected abstract T Create(string value);
    protected abstract string GetValue(T value);

    public sealed override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Create(reader.GetString() ?? string.Empty);

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(GetValue(value));
}
