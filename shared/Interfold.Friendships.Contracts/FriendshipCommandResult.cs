using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts;

public sealed record FriendshipCommandResult(SystemId SystemId, SystemId TargetSystemId, FriendshipAction Action, bool Replay) : ICommandResult<FriendshipCommandResult>
{
    public FriendshipCommandResult WithReplay() => this with { Replay = true };
}
