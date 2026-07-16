using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models;

// The Id here is the settings field definition's FieldId — alter field values are
// (field definition id → value) pairs joined against SettingsFieldReadModel.Id.
public sealed record AlterPublicFieldReadModel(FieldId Id, string Name, FieldType Type, string? Value);

public class BareAlter {
    public BareAlter(
        AlterId id,
        string name,
        AvatarUrl? avatarUrl,
        AvatarSource? avatarSource,
        HexColor? color,
        string? pronouns,
        string? description,
        IReadOnlyList<AlterPublicFieldReadModel> fields)
    {
        Id = id;
        Name = name;
        AvatarUrl = avatarUrl;
        AvatarSource = avatarSource;
        Color = color;
        Pronouns = pronouns;
        Fields = fields;
        Description = description;
    }

    public AlterId Id { get; set; }
    public AvatarUrl? AvatarUrl { get; set; }
    public AvatarSource? AvatarSource { get; set; }
    public HexColor? Color { get; set; }
    public string Name { get; set; }
    public string? Pronouns { get; set; }
    public IReadOnlyList<AlterPublicFieldReadModel> Fields { get; set; }
    public string? Description { get; set; }
 }

public sealed class AlterReadModel : BareAlter {

    public AlterReadModel(
        AlterId id,
        string name,
        string? description,
        AvatarUrl? avatarUrl,
        AvatarSource? avatarSource,
        HexColor? color,
        string? pronouns,
        VisibilityLevel securityLevel,
        IReadOnlyList<AlterPublicFieldReadModel> fields,
        string? proxyName,
        string? alias,
        bool? untracked,
        bool? archived,
        bool? pinned) : base(id, name, avatarUrl, avatarSource, color, pronouns, description, fields)
    {
        Alias = alias;
        SecurityLevel = securityLevel;
        ProxyName = proxyName;
        Untracked = untracked ?? false;
        Archived = archived ?? false;
        Pinned = pinned ?? false;
    }

    public string? Alias { get; set; }
    public VisibilityLevel SecurityLevel { get; set; }
    public string? ProxyName { get; set; }
    public bool Untracked { get; set; }
    public bool Archived { get; set; }
    public bool Pinned { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<VisibilityLevel>))]
public enum VisibilityLevel : short
{
    [JsonStringEnumMemberName("public")]
    Public = 0,
    [JsonStringEnumMemberName("friends_only")]
    FriendsOnly = 1,
    [JsonStringEnumMemberName("trusted_only")]
    TrustedOnly = 2,
    [JsonStringEnumMemberName("private")]
    Private = 3
}
