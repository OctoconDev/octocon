using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary><see cref="ISingletonTaskOwner"/> for primary nodes — always owns, runs
/// work inline (mirrors the legacy Horde DynamicSupervisor pattern).</summary>
public sealed class PrimaryOnlySingletonTaskOwner : ISingletonTaskOwner
{
    public bool OwnsTask(SingletonTaskName taskName) => true;

    public Task RunIfOwnerAsync(
        SingletonTaskName taskName,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
        => work(cancellationToken);
}
