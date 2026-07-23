using Interfold.Bootstrapper.Cli;
using Interfold.DatabaseBootstrap;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Tiny adapter from <see cref="PhaseLogger"/> (bootstrapper-specific) to
/// <see cref="IDatabaseInitLogger"/> (the framework-agnostic surface the shared seed
/// orchestrator consumes). Lives in the bootstrapper so <c>Interfold.DatabaseBootstrap</c>
/// can stay free of any concrete logging dependency.
/// </summary>
internal sealed class PhaseLoggerAdapter(PhaseLogger inner) : IDatabaseInitLogger
{
    public void Info(string message) => inner.Info(message);
    public void Warn(string message) => inner.Warn(message);
    public void Error(string message) => inner.Error(message);
}
