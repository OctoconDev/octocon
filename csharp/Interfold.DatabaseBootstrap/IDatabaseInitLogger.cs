namespace Interfold.DatabaseBootstrap;

/// <summary>Minimal logging surface for the database seed orchestrators. Bootstrapper
/// wraps its <c>PhaseLogger</c> behind this so the shared project stays framework-free.</summary>
public interface IDatabaseInitLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>Drops every log message. Handy in tests where seed progress is implied
/// by downstream assertions.</summary>
public sealed class NoOpDatabaseInitLogger : IDatabaseInitLogger
{
    public static readonly NoOpDatabaseInitLogger Instance = new();

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}
