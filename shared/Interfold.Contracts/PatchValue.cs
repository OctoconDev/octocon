using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts;

public enum PatchValueState
{
    /// <summary>The property was absent from the JSON payload — leave the target unchanged.</summary>
    Unset = 0,

    /// <summary>The property was an explicit JSON <c>null</c> — clear the target.</summary>
    Null = 1,

    /// <summary>The property carried a parseable value.</summary>
    Value = 2,

    /// <summary>
    /// The property was present but could not be parsed as <c>T</c>. Captured as a state
    /// (instead of letting the converter throw) so callers can return their own
    /// domain-specific error body rather than MVC's generic model-binding 400.
    /// </summary>
    Invalid = 3
}

/// <summary>
/// Tri-state JSON PATCH field: distinguishes "property absent" (don't touch) from
/// "property null" (clear) from "property has a value" (set). Sparse-diff PATCH bodies
/// (e.g. the Kotlin client's poll update) rely on this distinction, which a plain
/// nullable property cannot represent.
/// </summary>
[JsonConverter(typeof(PatchValueJsonConverterFactory))]
public readonly record struct PatchValue<T>
{
    public PatchValueState State { get; }

    /// <summary>Only meaningful when <see cref="State"/> is <see cref="PatchValueState.Value"/>.</summary>
    public T? Value { get; }

    private PatchValue(PatchValueState state, T? value)
    {
        State = state;
        Value = value;
    }

    public static PatchValue<T> Unset => default;
    public static PatchValue<T> OfNull() => new(PatchValueState.Null, default);
    public static PatchValue<T> Of(T value) => new(PatchValueState.Value, value);
    public static PatchValue<T> Invalid() => new(PatchValueState.Invalid, default);

    /// <summary>True when the property appeared in the payload (null, value, or invalid).</summary>
    public bool IsSet => State is not PatchValueState.Unset;

    public bool IsInvalid => State is PatchValueState.Invalid;
}

public sealed class PatchValueJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(PatchValue<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var inner = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(PatchValueJsonConverter<>).MakeGenericType(inner);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class PatchValueJsonConverter<T> : JsonConverter<PatchValue<T>>
{
    public override PatchValue<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return PatchValue<T>.OfNull();
        }

        // Materialize the whole value first so a failed parse can't leave the reader
        // mid-token; parse failures become the Invalid state instead of a JsonException.
        var element = JsonElement.ParseValue(ref reader);
        try
        {
            var value = element.Deserialize<T>(options);
            return value is null ? PatchValue<T>.OfNull() : PatchValue<T>.Of(value);
        }
        catch (JsonException)
        {
            return PatchValue<T>.Invalid();
        }
        catch (FormatException)
        {
            return PatchValue<T>.Invalid();
        }
    }

    public override void Write(Utf8JsonWriter writer, PatchValue<T> value, JsonSerializerOptions options)
    {
        if (value.State == PatchValueState.Value)
        {
            JsonSerializer.Serialize(writer, value.Value, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
