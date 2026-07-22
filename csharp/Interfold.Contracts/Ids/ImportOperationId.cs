using System.Text.Json;
using System.Text.Json.Serialization;

namespace Interfold.Contracts.Ids;

/// <summary>TimeUuid surrogate key of an <c>import_operations</c> row; also the public
/// correlation handle in HTTP + socket frames. Distinct from <see cref="OperationId"/>
/// (the command-vocabulary id string). JSON serializes as the standard <c>"D"</c> GUID.</summary>
[JsonConverter(typeof(ImportOperationIdJsonConverter))]
public readonly record struct ImportOperationId(Guid Value)
{
    public static readonly ImportOperationId Empty = new(Guid.Empty);

    public static explicit operator ImportOperationId(Guid value) => new(value);

    public static implicit operator Guid(ImportOperationId value) => value.Value;

    public override string ToString() => Value.ToString("D");
}

internal sealed class ImportOperationIdJsonConverter : JsonConverter<ImportOperationId>
{
    public override ImportOperationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, ImportOperationId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
