using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Socket.Contracts;

namespace Interfold.Settings.Contracts;

public sealed record SettingsFieldsUpdatedPayload(IReadOnlyList<SettingsFieldReadModel> Fields) : ISocketPayload;

public sealed record SettingsUsernameUpdatedPayload(Username Username) : ISocketPayload;

public sealed record SettingsSelfUpdatedPayload(SocketSelfReadModel Data) : ISocketPayload;

public sealed record DiscordAccountLinkedPayload(DiscordId DiscordId) : ISocketPayload;

public sealed record GoogleAccountLinkedPayload(Email Email) : ISocketPayload;

public sealed record AppleAccountLinkedPayload(AppleId AppleId) : ISocketPayload;

public sealed record ImportCompletedSocketPayload(int AlterCount) : ISocketPayload;
