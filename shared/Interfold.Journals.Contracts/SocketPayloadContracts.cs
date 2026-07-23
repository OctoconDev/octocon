using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;

namespace Interfold.Contracts;

public sealed record GlobalJournalSocketPayload(JournalReadModel Entry) : ISocketPayload;

public sealed record AlterJournalSocketPayload(AlterJournalReadModel Entry) : ISocketPayload;

public sealed record EntryDeletedSocketPayload(EntryId EntryId) : ISocketPayload;
