using Interfold.Journals.Contracts.Ids;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Journals.Contracts;

public sealed record GlobalJournalCommandResult(SystemId SystemId, EntryId EntryId, bool Replay) : ICommandResult<GlobalJournalCommandResult>
{
    public GlobalJournalCommandResult WithReplay() => this with { Replay = true };
}

public sealed record AlterJournalCommandResult(SystemId SystemId, EntryId EntryId, AlterId AlterId, bool Replay) : ICommandResult<AlterJournalCommandResult>
{
    public AlterJournalCommandResult WithReplay() => this with { Replay = true };
}
