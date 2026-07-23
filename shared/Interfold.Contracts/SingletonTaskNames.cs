namespace Interfold.Contracts;

/// <summary>
/// Typed name of a singleton background task gated by <c>ISingletonTaskOwner</c>.
/// The registry below is the closed vocabulary.
/// </summary>
public readonly record struct SingletonTaskName
{
    public string Value { get; }

    public SingletonTaskName(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;
}

/// <summary>Well-known singleton task names.</summary>
public static class SingletonTaskNames
{
    /// <summary>Batched push-notification flush for fronting changes.</summary>
    public static readonly SingletonTaskName FrontNotifier = new("front_notifier");

    /// <summary>Single-writer link-token issuer.</summary>
    public static readonly SingletonTaskName LinkTokenRegistry = new("link_token_registry");
}