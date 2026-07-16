using Interfold.Contracts.Secrets;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Synchronous read-only view over the subset of <see cref="ISecretsStore"/> rows that the
/// API's <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/> patchers
/// need to consult during options resolution.
///
/// <para>
/// <c>IPostConfigureOptions&lt;T&gt;.PostConfigure</c> is a synchronous callback fired inside
/// the options factory pipeline. It cannot await <c>ISecretsStore.GetAsync</c> without either
/// blocking a threadpool thread or requiring every consumer to accept an async options view
/// — both are anti-patterns for the DI-time snapshot pattern. The pre-Build loader resolves
/// the impedance mismatch by loading all interesting rows into this snapshot once before the
/// host builds (<see cref="SecretsPreBuildLoader"/>) and reading them synchronously here.
/// </para>
///
/// <para>
/// <c>IsPopulated</c> lets tests assert the loader ran before dereference. Consumers that
/// need to distinguish "loader hasn't run yet" from "row genuinely absent" can check the
/// flag; production consumers rely on <see cref="SecretsPreBuildLoader.Load"/> having run
/// synchronously in <c>Program.cs</c> before any DI registration (and therefore before any
/// options-factory pipeline can fire) and just call <see cref="Get"/> directly.
/// </para>
/// </summary>
public interface ISecretsSnapshot
{
    /// <summary>
    /// Returns the value for <paramref name="key"/>, or <c>null</c> if the row is absent /
    /// blank / the snapshot hasn't been populated yet. Never throws.
    /// </summary>
    string? Get(SecretsStoreKey key);

    /// <summary>
    /// <c>true</c> after <see cref="SecretsPreBuildLoader"/> has finished its
    /// <see cref="SecretsPreBuildLoader.Load"/> call, regardless of how many rows were
    /// actually present in the store.
    /// </summary>
    bool IsPopulated { get; }
}
