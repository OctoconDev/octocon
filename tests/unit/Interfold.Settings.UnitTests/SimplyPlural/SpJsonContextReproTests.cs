using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Interfold.Settings.Api.Services.SimplyPlural;
using SpJsonContext = Interfold.Settings.Api.Services.SimplyPlural.SpJsonContext;

namespace Interfold.Api.UnitTests.SimplyPlural;

// Pins SpCustomFieldContent.SupportMarkdown as nullable-bool. SP's update300 migration
// copies legacy field rows without backfilling supportMarkdown → wire sends null. A
// non-nullable bool would fail every legacy-migrated system's customFields deserialise
// (swallowed by FetchAsync's catch-all → silent custom-fields no-op).
public sealed class SpJsonContextReproTests
{
    [Test]
    public async Task CustomFields_WithNullSupportMarkdown_ShouldDeserializeWithoutThrowing()
    {
        var json = """[{"exists":true,"id":"000000000000000000000001","content":{"uid":"synthetic-system","name":"SyntheticField","order":"0|aaaaaa:","type":0,"supportMarkdown":null,"buckets":["000000000000000000000002","000000000000000000000003"],"oid":"synthetic-oid-placeholder"}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpCustomFieldContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpCustomFieldContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Count).IsEqualTo(1);
        await Assert.That(result[0].Content.Name).IsEqualTo("SyntheticField");
        await Assert.That(result[0].Content.SupportMarkdown).IsNull();
    }

    [Test]
    public async Task CustomFields_WithExplicitTrueSupportMarkdown_StillDeserializes()
    {
        var json = """[{"exists":true,"id":"000000000000000000000001","content":{"name":"x","type":0,"supportMarkdown":true}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpCustomFieldContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpCustomFieldContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result![0].Content.SupportMarkdown).IsTrue();
    }

    [Test]
    public async Task CustomFields_WithExplicitFalseSupportMarkdown_StillDeserializes()
    {
        var json = """[{"exists":true,"id":"000000000000000000000001","content":{"name":"x","type":0,"supportMarkdown":false}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpCustomFieldContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpCustomFieldContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result![0].Content.SupportMarkdown).IsFalse();
    }

    // Notes carry the same null shape from pre-supportMarkdown SP; not read today but a
    // hard-throw here would silently empty the per-alter notes page for any affected system.
    [Test]
    public async Task Notes_WithNullSupportMarkdown_ShouldDeserializeWithoutThrowing()
    {
        var json = """[{"exists":true,"id":"000000000000000000000004","content":{"title":"t","note":"n","color":"#000","member":"m","date":0,"supportMarkdown":null,"lastOperationTime":0}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpNoteContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpNoteContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result![0].Content.SupportMarkdown).IsNull();
    }
}
