using System;
using System.Collections.Generic;
using Cassandra;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;

namespace Interfold.Infrastructure.Scylla.Repository;

internal static class TagRowMappers
{
    public static TagReadModel MapTagReadModel(Row row, IReadOnlyList<AlterId> alterIds)
    {
        return new TagReadModel(
            new(row.GetValue<Guid>("id")),
            row.GetValue<string>("name"),
            HexColor.FromNullable(row.GetValue<string?>("color")),
            row.GetValue<string?>("description"),
            row.GetValue<Guid?>("parent_tag_id") is { } parentId ? new TagId(parentId) : null,
            alterIds,
            row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
            row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
            row.GetValue<short?>("security_level").FromCode<VisibilityLevel>(),
            new(row.GetValue<string>("user_id"))
        );
    }

    public static TagPublicReadModel MapTagPublicReadModel(Row row, IReadOnlyList<BareAlter> alters)
    {
        return new TagPublicReadModel(
            new(row.GetValue<Guid>("id")),
            row.GetValue<string>("name"),
            HexColor.FromNullable(row.GetValue<string?>("color")),
            row.GetValue<string?>("description"),
            row.GetValue<Guid?>("parent_tag_id") is { } parentId ? new TagId(parentId) : null,
            alters,
            row.GetValue<DateTimeOffset>("inserted_at").UtcDateTime,
            row.GetValue<DateTimeOffset>("updated_at").UtcDateTime,
            row.GetValue<short?>("security_level").FromCode<VisibilityLevel>(),
            new(row.GetValue<string>("user_id"))
        );
    }
}
