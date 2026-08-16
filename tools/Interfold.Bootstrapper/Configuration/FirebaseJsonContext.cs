using System.Text.Json.Serialization;
using Interfold.Bootstrapper.Phases;
using Interfold.Settings.Contracts.Configuration;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Source-gen contexts for <see cref="FirebasePhase"/> JSON I/O.
/// Naming policies differ per input (Google snake_case vs console camelCase) and the
/// seeded output is always snake_case — three contexts keep each policy compile-time.</summary>

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(FirebasePhase.GoogleServicesFile))]
[JsonSerializable(typeof(FirebasePhase.ServiceAccountStub))]
internal sealed partial class FirebaseSnakeCaseReadContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(FirebaseWebClientConfig))]
internal sealed partial class FirebaseCamelCaseReadContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FirebaseAndroidClientConfig))]
[JsonSerializable(typeof(FirebaseIosClientConfig))]
[JsonSerializable(typeof(FirebaseWebClientConfig))]
internal sealed partial class FirebaseSnakeCaseWriteContext : JsonSerializerContext;
