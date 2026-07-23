using Interfold.Shared.Contracts;

namespace Interfold.Domain.Abstractions;

/// <summary>Gates named singleton background tasks so only one node (usually the primary)
/// runs each (mirrors the legacy FrontNotifier / LinkTokenRegistry Horde pattern).</summary>
public interface ISingletonTaskOwner
{
    bool OwnsTask(SingletonTaskName taskName);

    /// <summary>No-op when this node isn't the owner.</summary>
    Task RunIfOwnerAsync(
        SingletonTaskName taskName,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}