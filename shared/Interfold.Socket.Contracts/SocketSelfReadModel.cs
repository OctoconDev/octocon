using System.Text.Json.Serialization;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Socket.Contracts;

// The four *Linked members keep the legacy identity-shaped wire names (discord_id etc.)
// but carry only the "SET"/null link-presence flag — see AccountLinkFlag. GoogleLinked is
// historically always null (Google links surface via email) and stays for wire shape.
public sealed record SocketSelfReadModel(
    SystemId Id,
    Username? Username,
    string? Description,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    [property: JsonPropertyName("discord_id")] AccountLinkFlag DiscordLinked,
    [property: JsonPropertyName("google_id")] AccountLinkFlag GoogleLinked,
    [property: JsonPropertyName("apple_id")] AccountLinkFlag AppleLinked,
    [property: JsonPropertyName("email")] AccountLinkFlag EmailLinked,
    AutoproxyMode AutoproxyMode,
    bool ShowSystemTag,
    int LifetimeAlterCount,
    AlterId? PrimaryFront,
    IReadOnlyList<SettingsFieldReadModel> Fields,
    bool EncryptionInitialized) : ISocketPayload, IAvatarBearing;
