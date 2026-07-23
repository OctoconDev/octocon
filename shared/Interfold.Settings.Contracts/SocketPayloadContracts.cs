using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

public sealed record SettingsFieldsUpdatedPayload(IReadOnlyList<SettingsFieldReadModel> Fields) : ISocketPayload;

public sealed record SettingsUsernameUpdatedPayload(Username Username) : ISocketPayload;

public sealed record SettingsSelfUpdatedPayload(SocketSelfReadModel Data) : ISocketPayload;

public sealed record DiscordAccountLinkedPayload(DiscordId DiscordId) : ISocketPayload;

public sealed record GoogleAccountLinkedPayload(Email Email) : ISocketPayload;

public sealed record AppleAccountLinkedPayload(AppleId AppleId) : ISocketPayload;

public sealed record ImportCompletedSocketPayload(int AlterCount) : ISocketPayload;
