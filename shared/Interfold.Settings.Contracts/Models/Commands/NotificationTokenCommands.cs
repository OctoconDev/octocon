using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

public sealed record AddPushTokenCommand(PushToken Token);

public sealed record RemovePushTokenCommand(PushToken Token);

public sealed record UpdateDescriptionCommand(string Description);
