using Interfold.Alters.Contracts.Abstractions;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Observability;
using Microsoft.Extensions.Logging;

namespace Interfold.Settings.Domain;

public sealed class AlterFieldDefinitionsAdapter : IAlterFieldDefinitions
{
    private readonly ISettingsFieldRepository _settingsFields;
    private readonly ILogger<AlterFieldDefinitionsAdapter> _logger;

    public AlterFieldDefinitionsAdapter(
        ISettingsFieldRepository settingsFields,
        ILogger<AlterFieldDefinitionsAdapter> logger)
    {
        _settingsFields = settingsFields;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SettingsFieldReadModel>> ListVisibleAsync(
        SystemId systemId,
        FriendshipLevel? friendshipLevel,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var definitions = await _settingsFields.ListAsync(systemId, cancellationToken).ConfigureAwait(false);
            return definitions
                .Where(def => def.SecurityLevel.CanBeViewedBy(friendshipLevel))
                .ToArray();
        }
        catch (Exception ex)
        {
            GuardedMetrics.ErrorsTotal.Add(1,
                new KeyValuePair<string, object?>("entity_type", "field"),
                new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            _logger.LogError(ex,
                "Guarded field-definition load failed: system={SystemId}, friendship_level={FriendshipLevel}",
                systemId.Value, friendshipLevel);
            throw;
        }
    }
}
