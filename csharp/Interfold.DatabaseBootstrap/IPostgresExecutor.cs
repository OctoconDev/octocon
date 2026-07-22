namespace Interfold.DatabaseBootstrap;

/// <summary>Transport surface for <see cref="PostgresSeeder"/> — implemented by both
/// the compose-exec (<c>docker compose exec ... psql</c>) and in-process Npgsql
/// transports. Every call takes user/password/database because the seed flow swaps
/// roles (init → admin) and databases (postgres → app) between steps.</summary>
public interface IPostgresExecutor
{
    /// <summary>Run a DDL/DML script; throws after the transport's own transient-retry
    /// budget is exhausted.</summary>
    Task ExecScriptAsync(string user, string password, string database, string sql, CancellationToken ct);

    /// <summary>Run a parameterised statement. Compose-exec binds via
    /// <c>psql -v name=value</c> + <c>:'name'</c>; Npgsql binds <see cref="System.Data.Common.DbParameter"/>
    /// values with matching names.</summary>
    Task ExecScriptWithVarsAsync(
        string user,
        string password,
        string database,
        string sql,
        IReadOnlyList<(string Name, string Value)> vars,
        CancellationToken ct);

    /// <summary>First column of the first row, or <c>null</c>. Used for idempotency
    /// probes — never masks transient errors as success, so callers must guard.</summary>
    Task<string?> ExecScalarAsync(string user, string password, string database, string sql, CancellationToken ct);
}
