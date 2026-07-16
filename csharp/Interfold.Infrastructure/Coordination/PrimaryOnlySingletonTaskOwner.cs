using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// <see cref="ISingletonTaskOwner"/> for <see cref="NodeGroup.Primary"/> nodes.
/// Runs the supplied work inline; always reports ownership.
/// Mirrors the legacy Horde DynamicSupervisor pattern where only primary nodes
/// start <c>Global.FrontNotifier</c> and <c>Global.LinkTokenRegistry</c>.
/// </summary>
public sealed class PrimaryOnlySingletonTaskOwner : ISingletonTaskOwner
{
    public bool OwnsTask(SingletonTaskName taskName) => true;

    public Task RunIfOwnerAsync(
        SingletonTaskName taskName,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
        => work(cancellationToken);
}
