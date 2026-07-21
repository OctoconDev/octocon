using System.Collections.Concurrent;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaSettingsFieldRepository : ISettingsFieldRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaScopeResolver _scopeResolver;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private static readonly ConcurrentDictionary<(int ClusterId, string Keyspace), byte> UdtMappings = new();

    public ScyllaSettingsFieldRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaScopeResolver scopeResolver,
        IScyllaKeyspaceResolver keyspaceResolver,
        IOptions<PersistenceConfiguration> options)
    {
        _sessionProvider = sessionProvider;
        _scopeResolver = scopeResolver;
        _keyspaceResolver = keyspaceResolver;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var fields = await LoadFieldsWithMappingAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return Array.Empty<SettingsFieldReadModel>();
            }

            var result = fields
                .Select((field, index) => new SettingsFieldReadModel(
                    new(field.Id),
                    field.Name,
                    field.Type.FromCode<FieldType>(),
                    field.SecurityLevel.FromCode<VisibilityLevel>(),
                    field.Locked,
                    index,
                    field.InsertedAt?.UtcDateTime))
                .ToArray();

            return (IReadOnlyList<SettingsFieldReadModel>)result;
        }, cancellationToken);
    }

    public async Task<FieldId?> CreateAsync(
        SystemId systemId,
        string name,
        FieldType type,
        VisibilityLevel securityLevel,
        bool locked,
        DateTime insertedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync<FieldId?>(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var fields = await LoadFieldsWithMappingAsync(session, keyspace, normalizedSystemId) ?? [];

            var fieldId = Guid.NewGuid();
            fields.Add(CreateFieldUdt(session, keyspace, fieldId, name, type, securityLevel, locked, insertedAtUtc));

            await session.ExecuteAsync(BuildPersistFieldsStatement(keyspace, fields, normalizedSystemId));

            return new(fieldId);
        }, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        FieldId fieldId,
        string? name,
        VisibilityLevel? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var fields = await LoadFieldsWithMappingAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return false;
            }

            var updated = false;
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Id != fieldId.Value)
                {
                    continue;
                }

                if (name is not null)
                {
                    fields[i].Name = name;
                }

                if (securityLevel is not null)
                {
                    fields[i].SecurityLevel = (short)securityLevel.Value;
                }

                if (locked is not null)
                {
                    fields[i].Locked = locked.Value;
                }

                fields[i].UpdatedAt = DateTimeOffset.UtcNow;
                updated = true;
                break;
            }

            if (!updated)
            {
                return false;
            }

            await session.ExecuteAsync(BuildPersistFieldsStatement(keyspace, fields, normalizedSystemId));

            return true;
        }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(SystemId systemId, FieldId fieldId, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var fields = await LoadFieldsWithMappingAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return false;
            }

            var removed = fields.RemoveAll(f => f.Id == fieldId.Value) > 0;
            if (!removed)
            {
                return false;
            }

            // Remove the field values from all alters before deleting the field itself
            // We want to ensure that the field values are removed to ensure no leakage of deleted field data
            var batch = await RemoveFieldValuesFromAltersAsync(session, keyspace, normalizedSystemId, fieldId.Value);
            batch.Add(BuildPersistFieldsStatement(keyspace, fields, normalizedSystemId));

            await session.ExecuteAsync(batch);

            return true;
        }, cancellationToken);
    }

    public async Task<bool> RelocateAsync(SystemId systemId, FieldId fieldId, int index, CancellationToken cancellationToken = default)
    {
        return await _scopeResolver.ExecuteAsync(systemId, async scope =>
        {
            var (session, keyspace, normalizedSystemId) = scope;
            var fields = await LoadFieldsWithMappingAsync(session, keyspace, normalizedSystemId);
            if (fields is null)
            {
                return false;
            }

            var currentIndex = fields.FindIndex(f => f.Id == fieldId.Value);
            if (currentIndex < 0)
            {
                return false;
            }

            var field = fields[currentIndex];
            field.UpdatedAt = DateTimeOffset.UtcNow;
            fields.RemoveAt(currentIndex);

            var boundedIndex = Math.Max(0, Math.Min(index, fields.Count));
            fields.Insert(boundedIndex, field);

            await session.ExecuteAsync(BuildPersistFieldsStatement(keyspace, fields, normalizedSystemId));

            return true;
        }, cancellationToken);
    }

    private static async Task<List<UserFieldUdt>?> LoadFieldsAsync(ISession session, string keyspace, string normalizedSystemId)
    {
        var query = new SimpleStatement(
            $"SELECT fields FROM {keyspace}.users WHERE id = ? LIMIT 1",
            normalizedSystemId);

        var row = (await session.ExecuteAsync(query)).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return row.GetValue<IEnumerable<UserFieldUdt>?>("fields")?.ToList() ?? [];
    }

    private static async Task<List<UserFieldUdt>?> LoadFieldsWithMappingAsync(ISession session, string keyspace, string normalizedSystemId)
    {
        EnsureFieldUdtMapping(session, keyspace);
        return await LoadFieldsAsync(session, keyspace, normalizedSystemId);
    }

    private static UserFieldUdt CreateFieldUdt(
        ISession session,
        string keyspace,
        Guid id,
        string name,
        FieldType type,
        VisibilityLevel securityLevel,
        bool locked,
        DateTime insertedAtUtc)
    {
        EnsureFieldUdtMapping(session, keyspace);
        // Force Utc kind so we never construct a DateTimeOffset from a Local DateTime (which
        // would silently apply the host's offset). Callers all hand us UTC values today
        // (importer: ObjectId-derived; handler: OccurredAt.UtcDateTime), but SpecifyKind
        // makes the contract explicit and defensive against future producers.
        var insertedAtOffset = new DateTimeOffset(DateTime.SpecifyKind(insertedAtUtc, DateTimeKind.Utc), TimeSpan.Zero);
        return new UserFieldUdt
        {
            Id = id,
            Name = name,
            Type = (short)type,
            Locked = locked,
            SecurityLevel = (short)securityLevel,
            InsertedAt = insertedAtOffset,
            UpdatedAt = insertedAtOffset
        };
    }

    private static SimpleStatement BuildPersistFieldsStatement(
        string keyspace,
        IReadOnlyCollection<UserFieldUdt> fields,
        string normalizedSystemId)
        => new(
            $"UPDATE {keyspace}.users SET fields = ?, updated_at = toTimestamp(now()) WHERE id = ?",
            fields,
            normalizedSystemId);

    private static void EnsureFieldUdtMapping(ISession session, string keyspace)
    {
        var key = (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(session.Cluster), keyspace);
        if (UdtMappings.ContainsKey(key))
        {
            return;
        }

        session.UserDefinedTypes.Define(
            UdtMap
                .For<UserFieldUdt>("field", keyspace)
                .Map(f => f.Id, "id")
                .Map(f => f.Name, "name")
                .Map(f => f.Type, "type")
                .Map(f => f.Locked, "locked")
                .Map(f => f.SecurityLevel, "security_level")
                .Map(f => f.InsertedAt, "inserted_at")
                .Map(f => f.UpdatedAt, "updated_at"));

        UdtMappings.TryAdd(key, 0);
    }

    private static async Task<BatchStatement> RemoveFieldValuesFromAltersAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        Guid fieldId)
    {
        ScyllaAlterRepository.EnsureAlterFieldUdtMapping(session, keyspace);

        var rows = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, fields FROM {keyspace}.alters WHERE user_id = ?",
            normalizedSystemId));

        var batch = new BatchStatement();

        foreach (var row in rows)
        {
            var alterId = row.GetValue<short>("id");
            var currentFields = row.GetValue<IEnumerable<AlterFieldUdt>?>("fields")?.ToList();
            if (currentFields is null || currentFields.Count == 0)
            {
                continue;
            }

            var removedAny = currentFields.RemoveAll(x => x.Id == fieldId) > 0;
            if (!removedAny)
            {
                continue;
            }

            batch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.alters SET fields = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                currentFields,
                normalizedSystemId,
                alterId));
        }

        return batch;
    }

    // The Cassandra C# driver's UDT serializer routes `timestamp` columns through
    // DateTimeOffset; nullable DateTime properties throw `InvalidTypeException: No converter
    // is available from Nullable<DateTime> is not convertible to type DateTimeOffset` on
    // write. We keep the public API on `DateTime`/`DateTime?` (matching every other repo)
    // and only carry DateTimeOffset across the UDT boundary.
    private sealed class UserFieldUdt
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public short Type { get; set; }
        public bool Locked { get; set; }
        public short SecurityLevel { get; set; }
        public DateTimeOffset? InsertedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
