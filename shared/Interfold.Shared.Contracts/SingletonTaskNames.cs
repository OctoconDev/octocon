namespace Interfold.Shared.Contracts;

/// <summary>Typed name of a singleton background task gated by <c>ISingletonTaskOwner</c>.</summary>
public readonly record struct SingletonTaskName
{
    public string Value { get; }

    public SingletonTaskName(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;
}
