using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Contracts;

public sealed record GlobalJournalSocketPayload(JournalReadModel Entry) : ISocketPayload;

public sealed record AlterJournalSocketPayload(AlterJournalReadModel Entry) : ISocketPayload;

public sealed record EntryDeletedSocketPayload(EntryId EntryId) : ISocketPayload;
