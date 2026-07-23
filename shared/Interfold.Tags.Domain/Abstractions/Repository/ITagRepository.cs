using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Domain.Abstractions.Repository;

public interface ITagRepository
{
    /// <summary>Returns the new tag's ID on success, null if the parent was not found.</summary>
    Task<TagId?> CreateAsync(SystemId systemId, CreateTagCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the tag was found and updated, false if not found.</summary>
    Task<bool> UpdateAsync(SystemId systemId, UpdateTagCommand command, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the tag was found and deleted, false if not found.</summary>
    Task<bool> DeleteAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the alter was attached, false if the tag does not exist.</summary>
    Task<bool> AttachAlterAsync(SystemId systemId, TagId tagId, AlterId alterId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the alter was detached, false if the tag/alter combo does not exist.</summary>
    Task<bool> DetachAlterAsync(SystemId systemId, TagId tagId, AlterId alterId, CancellationToken cancellationToken = default);

    /// <summary>Returns the immediate parent tag ID, or null if none or tag does not exist.</summary>
    Task<TagId?> GetParentIdAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the parent was set, false if the tag or parent tag does not exist.</summary>
    Task<bool> SetParentAsync(SystemId systemId, TagId tagId, TagId parentTagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the parent was removed, false if the tag does not exist.</summary>
    Task<bool> RemoveParentAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagReadModel>> ListAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<TagReadModel?> GetAsync(SystemId systemId, TagId tagId, CancellationToken cancellationToken = default);

    Task<TagPublicReadModel?> GetGuardedAsync(
        SystemId systemId,
        TagId tagId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default
    );
}
