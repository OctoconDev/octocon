using System.Collections.Concurrent;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemorySettingsFieldRepository : ISettingsFieldRepository
{
    private readonly IRegionContext _regionContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<ScopedSystemId, List<SettingsFieldReadModel>> _bySystem = new();

    public InMemorySettingsFieldRepository(IRegionContext regionContext, IServiceProvider serviceProvider)
    {
        _regionContext = regionContext;
        _serviceProvider = serviceProvider;
    }

    public Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        if (!TryGetStore(systemId, out var store))
        {
            return Task.FromResult<IReadOnlyList<SettingsFieldReadModel>>(Array.Empty<SettingsFieldReadModel>());
        }

        lock (store)
        {
            var result = store
                .OrderBy(x => x.Index)
                .Select(x => x)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SettingsFieldReadModel>>(result);
        }
    }

    public Task<FieldId?> CreateAsync(
        SystemId systemId,
        string name,
        FieldType type,
        VisibilityLevel securityLevel,
        bool locked,
        DateTime insertedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        var store = _bySystem.GetOrAdd(systemKey, _ => new List<SettingsFieldReadModel>());
        FieldId fieldId = new(Guid.NewGuid());

        lock (store)
        {
            store.Add(new SettingsFieldReadModel(fieldId, name, type, securityLevel, locked, store.Count, insertedAtUtc));
        }

        return Task.FromResult<FieldId?>(fieldId);
    }

    public Task<bool> UpdateAsync(
        SystemId systemId,
        FieldId fieldId,
        string? name,
        VisibilityLevel? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetStore(systemId, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var index = store.FindIndex(x => x.Id == fieldId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            var existing = store[index];
            store[index] = existing with
            {
                Name = name ?? existing.Name,
                SecurityLevel = securityLevel ?? existing.SecurityLevel,
                Locked = locked ?? existing.Locked
            };
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(SystemId systemId, FieldId fieldId, CancellationToken cancellationToken = default)
    {
        if (!TryGetStore(systemId, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var index = store.FindIndex(x => x.Id == fieldId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            store.RemoveAt(index);
            Reindex(store);
        }

        // Cascade delete field values from alters in the in-memory alter repository (if present)
        try
        {
            var alterRepo = _serviceProvider?.GetService<IAlterRepository>();
            if (alterRepo is InMemoryAlterRepository regional)
            {
                regional.RemoveFieldValuesForSystem(systemId, fieldId.Value);
            }
        }
        catch
        {
            // Best-effort cascade; don't fail deletion if alters update isn't available
        }

        return Task.FromResult(true);
    }

    public Task<bool> RelocateAsync(SystemId systemId, FieldId fieldId, int index, CancellationToken cancellationToken = default)
    {
        if (!TryGetStore(systemId, out var store))
            return Task.FromResult(false);

        lock (store)
        {
            var oldIndex = store.FindIndex(x => x.Id == fieldId);
            if (oldIndex < 0)
            {
                return Task.FromResult(false);
            }

            var field = store[oldIndex];
            store.RemoveAt(oldIndex);

            var boundedIndex = Math.Max(0, Math.Min(index, store.Count));
            store.Insert(boundedIndex, field);
            Reindex(store);
            return Task.FromResult(true);
        }
    }

    private static void Reindex(List<SettingsFieldReadModel> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            fields[i] = fields[i] with { Index = i };
        }
    }

    private bool TryGetStore(SystemId systemId, out List<SettingsFieldReadModel> store)
    {
        var systemKey = InMemoryStorageKeys.ForSystem(_regionContext, systemId);
        return _bySystem.TryGetValue(systemKey, out store!);
    }
}

