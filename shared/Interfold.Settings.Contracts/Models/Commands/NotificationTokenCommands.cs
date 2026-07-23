using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models.Commands;

public sealed record AddPushTokenCommand(PushToken Token);

public sealed record RemovePushTokenCommand(PushToken Token);

public sealed record UpdateDescriptionCommand(string Description);
