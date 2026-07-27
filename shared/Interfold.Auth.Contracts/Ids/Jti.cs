using System.Text.Json.Serialization;

namespace Interfold.Shared.Contracts.Ids;

/// <summary>
/// JWT ID (jti claim) used for token revocation tracking. DB binds unwrap with
/// <see cref="Value"/> (Npgsql cannot bind the struct). <c>ToString()</c> redacts.
/// </summary>
[JsonConverter(typeof(JtiJsonConverter))]
public readonly record struct Jti
{
    public string Value { get; }

    public Jti(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static explicit operator Jti(string value) => new(value);

    public override string ToString() => SecretRedaction.Redact(Value);

    /// <summary>Total function: null/empty/whitespace input → null wrapper.</summary>
    public static Jti? From(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new Jti(value);

    /// <summary>Mint a fresh JWT ID (32-char lowercase hex, no dashes).</summary>
    public static Jti NewJti() => new(Guid.NewGuid().ToString("N"));
}

internal sealed class JtiJsonConverter : StringBackedJsonConverter<Jti>
{
    protected override Jti Create(string value) => new(value);
    protected override string GetValue(Jti value) => value.Value;
}
