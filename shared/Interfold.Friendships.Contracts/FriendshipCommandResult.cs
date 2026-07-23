using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts;

public sealed record FriendshipCommandResult(SystemId SystemId, SystemId TargetSystemId, FriendshipAction Action, bool Replay) : ICommandResult<FriendshipCommandResult>
{
    public FriendshipCommandResult WithReplay() => this with { Replay = true };
}
