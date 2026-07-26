using Interfold.Settings.Contracts.Ids;

namespace Interfold.Settings.Contracts.Models.Commands;

public sealed record AddPushTokenCommand(PushToken Token);

public sealed record RemovePushTokenCommand(PushToken Token);

public sealed record UpdateDescriptionCommand(string Description);
