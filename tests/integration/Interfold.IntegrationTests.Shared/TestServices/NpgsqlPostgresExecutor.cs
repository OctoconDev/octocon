using Interfold.DatabaseBootstrap;
using Npgsql;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// <see cref="IPostgresExecutor"/> implementation that talks to Postgres in-process via
/// <see cref="NpgsqlConnection"/>. Used by the TUnit.Aspire fixtures (single-node Scylla,
/// multi-node Scylla, Cassandra) once they've waited for the <c>msg-db</c> resource to come
/// up. The bootstrapper uses a different implementation
/// (<c>ComposeExecPostgresExecutor</c>) that shells out to <c>docker compose exec</c>.
/// </summary>
/// <remarks>
/// <para>
/// Carries a base connection string in its constructor; per-call user/password/database
/// overrides rewrite the connection string via <see cref="NpgsqlConnectionStringBuilder"/>.
/// Connections are opened + disposed per call. Npgsql's built-in pooler caches the
/// underlying TCP sockets so this stays cheap.
/// </para>
/// <para>
/// Parameter binding bridge: <see cref="PostgresSqlTemplates.UpsertSecretSql"/> uses psql's
/// <c>:'name'</c> variable syntax (canonical for the compose-exec path). For Npgsql we
/// rewrite those into <c>@name</c> placeholders before sending so the same template body
/// works in-process. Mapping is by var-name lookup, not regex, so unrelated colons in the
/// SQL are untouched.
/// </para>
/// </remarks>
internal sealed class NpgsqlPostgresExecutor(string baseConnectionString) : IPostgresExecutor
{
    public Task ExecScriptAsync(string user, string password, string database, string sql, CancellationToken ct)
        => RunNonQueryAsync(user, password, database, sql, vars: null, ct);

    public Task ExecScriptWithVarsAsync(
        string user, string password, string database, string sql,
        IReadOnlyList<(string Name, string Value)> vars, CancellationToken ct)
        => RunNonQueryAsync(user, password, database, sql, vars, ct);

    public async Task<string?> ExecScalarAsync(string user, string password, string database, string sql, CancellationToken ct)
    {
        await using var conn = OpenConnection(user, password, database);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result switch
        {
            null => null,
            DBNull => null,
            _ => result.ToString(),
        };
    }

    private async Task RunNonQueryAsync(
        string user, string password, string database, string sql,
        IReadOnlyList<(string Name, string Value)>? vars, CancellationToken ct)
    {
        await using var conn = OpenConnection(user, password, database);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var rewritten = vars is null ? sql : RewriteBindPlaceholders(sql, vars);
        await using var cmd = new NpgsqlCommand(rewritten, conn);
        if (vars is not null)
        {
            foreach (var (name, value) in vars)
            {
                cmd.Parameters.AddWithValue(name, value);
            }
        }
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private NpgsqlConnection OpenConnection(string user, string password, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Username = user,
            Password = password,
            Database = database,
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    private static string RewriteBindPlaceholders(string sql, IReadOnlyList<(string Name, string Value)> vars)
    {
        // psql uses `:'name'` for safe quoted-literal substitution; Npgsql uses `@name` as a
        // typed bind parameter. We only rewrite the names we know we're about to bind so any
        // other colon-prefixed token in the SQL (e.g. inside a CAST expression) is left alone.
        var rewritten = sql;
        foreach (var (name, _) in vars)
        {
            rewritten = rewritten.Replace($":'{name}'", $"@{name}", StringComparison.Ordinal);
        }
        return rewritten;
    }
}
