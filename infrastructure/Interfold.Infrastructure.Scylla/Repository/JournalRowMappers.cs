using System;
using Cassandra;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Infrastructure.Scylla.Repository;

internal static class JournalRowMappers
{
    public static AlterJournalReadModel MapAlterJournalReadModel(Row row)
    {
        return new AlterJournalReadModel(
            new(row.GetValue<Guid>("id")),
            new(row.GetValue<string>("user_id")),
            new(row.GetValue<short>("alter_id")),
            row.GetValue<string>("title"),
            row.GetValue<string?>("content"),
            HexColor.FromNullable(row.GetValue<string?>("color")),
            row.GetValue<bool>("locked"),
            row.GetValue<bool>("pinned"),
            row.GetValue<DateTime>("inserted_at"),
            row.GetValue<DateTime>("updated_at")
        );
    }

    public static JournalReadModel MapJournalReadModel(Row row, AlterId[] alterIds)
    {
        return new JournalReadModel(
            new(row.GetValue<Guid>("id")),
            new(row.GetValue<string>("user_id")),
            row.GetValue<string>("title"),
            row.GetValue<string?>("content"),
            HexColor.FromNullable(row.GetValue<string?>("color")),
            row.GetValue<bool>("locked"),
            row.GetValue<bool>("pinned"),
            row.GetValue<DateTime>("inserted_at"),
            row.GetValue<DateTime>("updated_at"),
            alterIds
        );
    }
}
