using Aspire.Hosting.ApplicationModel;

namespace Interfold.AppHost.DevSeed;

/// <summary>
/// Marker <see cref="IResource"/> so <c>apiProject.WaitFor(seedResource)</c> can gate on
/// the dev-mode Postgres + Scylla seed completing. Owns no runtime state — the actual
/// wait+seed loop lives in <see cref="DevSeedHostedService"/>, which publishes the
/// <see cref="KnownResourceStates.Starting"/>/<see cref="KnownResourceStates.Running"/>/
/// <see cref="KnownResourceStates.FailedToStart"/> transitions on this resource.
/// </summary>
internal sealed class DevSeedResource(string name) : Resource(name);
