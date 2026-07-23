using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts;

public sealed record AlterCommandResult(SystemId SystemId, AlterId AlterId, bool Replay) : ICommandResult<AlterCommandResult>
{
    public AlterCommandResult WithReplay() => this with { Replay = true };
}
