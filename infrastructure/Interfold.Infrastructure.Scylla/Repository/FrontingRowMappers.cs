using System;
using Cassandra;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Infrastructure.Scylla.Repository;

internal static class FrontingRowMappers
{
    public static FrontHistoryReadModel MapFrontHistoryReadModel(Row row, SystemId systemId, DateTimeOffset? timeEnd = null)
    {
        return new FrontHistoryReadModel(
            new(row.GetValue<Guid>("id")),
            new(row.GetValue<short>("alter_id")),
            row.GetValue<string?>("comment"),
            row.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow,
            timeEnd,
            systemId
        );
    }
}
