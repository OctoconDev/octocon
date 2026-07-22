using Interfold.Contracts.Secrets;

namespace Interfold.Api.Services.Secrets;

/// <summary>Synchronous read-only view over the <see cref="ISecretsStore"/> rows the API's
/// <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> patchers
/// consult during options resolution. PostConfigure is sync so we can't await the store;
/// <see cref="SecretsPreBuildLoader"/> hydrates this snapshot pre-Build.</summary>
public interface ISecretsSnapshot
{
    /// <summary>Value for <paramref name="key"/>, or null if absent/blank/unpopulated. Never throws.</summary>
    string? Get(SecretsStoreKey key);

    /// <summary>True once <see cref="SecretsPreBuildLoader.Load"/> has returned.</summary>
    bool IsPopulated { get; }
}
