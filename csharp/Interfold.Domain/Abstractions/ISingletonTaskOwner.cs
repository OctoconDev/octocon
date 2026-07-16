using Interfold.Contracts;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Owns (or conditionally gates) a named singleton background task.
/// <para>
/// Mirrors the legacy pattern where only <c>primary</c> nodes start
/// <c>Octocon.Global.FrontNotifier</c> and <c>Octocon.Global.LinkTokenRegistry</c>
/// via the Horde DynamicSupervisor.
/// </para>
/// </summary>
public interface ISingletonTaskOwner
{
    /// <summary>
    /// Returns <see langword="true"/> if this node should run the named singleton task.
    /// </summary>
    bool OwnsTask(SingletonTaskName taskName);

    /// <summary>
    /// Runs <paramref name="work"/> if this node owns <paramref name="taskName"/>;
    /// otherwise returns <see cref="Task.CompletedTask"/> immediately.
    /// </summary>
    Task RunIfOwnerAsync(
        SingletonTaskName taskName,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}