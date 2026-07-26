using Interfold.Journals.Contracts.Ids;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Socket.Contracts;

namespace Interfold.Journals.Contracts;

public sealed record GlobalJournalSocketPayload(JournalReadModel Entry) : ISocketPayload;

public sealed record AlterJournalSocketPayload(AlterJournalReadModel Entry) : ISocketPayload;

public sealed record EntryDeletedSocketPayload(EntryId EntryId) : ISocketPayload;
