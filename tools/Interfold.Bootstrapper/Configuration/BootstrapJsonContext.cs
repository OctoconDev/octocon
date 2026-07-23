using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> for the bootstrapper's
/// JSON shapes. Keeps the bootstrapper AOT/trim-friendly even though Aspire itself isn't yet (the long-term
/// motivation for picking TUnit on the test side too).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BootstrapConfig))]
[JsonSerializable(typeof(GeneratedSecrets))]
public sealed partial class BootstrapJsonContext : JsonSerializerContext
{
}
