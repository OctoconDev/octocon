using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Interfold.Api.Services.SimplyPlural;

[JsonSerializable(typeof(SpEntity<SpSystemContent>))]
[JsonSerializable(typeof(List<SpEntity<SpCustomFieldContent>>))]
[JsonSerializable(typeof(List<SpEntity<SpMemberContent>>))]
[JsonSerializable(typeof(List<SpEntity<SpGroupContent>>))]
[JsonSerializable(typeof(List<SpEntity<SpFrontContent>>))]
[JsonSerializable(typeof(List<SpEntity<SpPollContent>>))]
[JsonSerializable(typeof(List<SpEntity<SpNoteContent>>))]
[JsonSerializable(typeof(SpPollContent))]
[JsonSerializable(typeof(SpNoteContent))]
[JsonSerializable(typeof(SpEntity<SpPollContent>))]
[JsonSerializable(typeof(SpEntity<SpNoteContent>))]
internal partial class SpJsonContext : JsonSerializerContext
{
}

