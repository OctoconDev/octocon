namespace Interfold.Shared.Contracts;

/// <summary>Fronting-owned singleton task names.</summary>
public static class FrontingTaskNames
{
    /// <summary>Batched push-notification flush for fronting changes.</summary>
    public static readonly SingletonTaskName FrontNotifier = new("front_notifier");
}
