using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Fixups;

/// <summary>One-shot data fixup that normalises legacy <c>color</c> columns across
/// every colour-bearing table so <see cref="HexColor.FromNullable"/> can rely on
/// <see cref="HexColor.IsWellFormed"/> as its precondition. Runs once per
/// (keyspace, table) pair via a ledger row in <c>global.schema_migrations</c> with
/// scope <c>hex_color_fixup:{keyspace}:{table}</c>; on a fresh cluster the ledger row
/// is still written so subsequent boots skip the scan.</summary>
public sealed class HexColorFixupService(
    IOptions<PersistenceConfiguration> options,
    IScyllaSessionProvider sessionProvider,
    IScyllaConfigResolver configResolver,
    ILogger<HexColorFixupService> logger) : IHostedLifecycleService
{
    private const string LedgerTableFqn = "global.schema_migrations";
    private const string FixupVersion = "v1";

    // Bump to invalidate previously-recorded fixup runs when normalisation changes.
    private const string FixupChecksum = "hex_color_fixup_v1_normalise_bare_hex_or_null";

    // alter_journals_by_alter is a denormalisation of alter_journals and must be fixed
    // up independently so the two tables stay in sync.
    private static readonly ColorTable[] ColorTables =
    [
        new("alters", ["user_id", "id"]),
        new("tags", ["user_id", "id"]),
        new("alter_journals", ["user_id", "id", "alter_id"]),
        new("alter_journals_by_alter", ["user_id", "alter_id", "id"]),
        new("global_journals", ["user_id", "id"]),
    ];

    private static readonly string[] RegionalKeyspaces =
        Enum.GetValues<ScyllaKeyspace>()
            .Select(EnumWire<ScyllaKeyspace>.ToWire)
            .ToArray();

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // ScyllaMigrationService runs first and owns the reachability decision — a session
        // failure here is expected on in-memory / bootstrap-only runs.
        ISession session;
        try
        {
            session = await sessionProvider.GetSessionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex,
                "[hex-color-fixup] Skipping: no Scylla session available. This is expected in in-memory / bootstrap-only runs.");
            return;
        }

        var keyspaces = TargetKeyspaces();
        var applied = await LoadAppliedFixupScopesAsync(session, keyspaces, cancellationToken);

        foreach (var keyspace in keyspaces)
        {
            foreach (var table in ColorTables)
            {
                var scope = $"hex_color_fixup:{keyspace}:{table.Name}";
                if (applied.TryGetValue(scope, out var existingChecksum)
                    && string.Equals(existingChecksum, FixupChecksum, StringComparison.Ordinal))
                {
                    logger.LogDebug("[hex-color-fixup] Skipping '{Scope}' — already applied.", scope);
                    continue;
                }

                var (normalised, nulled) = await FixupTableAsync(session, keyspace, table, cancellationToken);
                await RecordFixupAsync(session, scope, cancellationToken);
                logger.LogInformation(
                    "[hex-color-fixup] {Scope}: normalised {Normalised} row(s), nulled {Nulled} row(s).",
                    scope, normalised, nulled);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string[] TargetKeyspaces() =>
        options.Value.IsSingleScyllaInstance
            ? [configResolver.GetKeyspace()]
            : RegionalKeyspaces;

    private async Task<(int normalised, int nulled)> FixupTableAsync(
        ISession session,
        string keyspace,
        ColorTable table,
        CancellationToken cancellationToken)
    {
        var pkList = string.Join(", ", table.PrimaryKeyColumns);
        var selectStmt = new SimpleStatement(
            $"SELECT {pkList}, color FROM {keyspace}.{table.Name}");
        selectStmt.SetPageSize(500);

        var rows = await session.ExecuteAsync(selectStmt);

        var normalised = 0;
        var nulled = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = row.GetValue<string?>("color");
            if (existing is null)
                continue;

            var canonical = HexColor.Normalise(existing);
            if (string.Equals(canonical, existing, StringComparison.Ordinal))
                continue;

            var pkValues = table.PrimaryKeyColumns.Select(pk => row.GetValue<object?>(pk)).ToArray();
            var whereClause = string.Join(" AND ", table.PrimaryKeyColumns.Select(pk => $"{pk} = ?"));
            var updateArgs = new object?[pkValues.Length + 1];
            // canonical may be null; that intentionally nulls the color column.
            updateArgs[0] = canonical;
            Array.Copy(pkValues, 0, updateArgs, 1, pkValues.Length);

            var updateStmt = new SimpleStatement(
                $"UPDATE {keyspace}.{table.Name} SET color = ? WHERE {whereClause}",
                updateArgs);

            await session.ExecuteAsync(updateStmt);

            if (canonical is null) nulled++;
            else normalised++;
        }

        return (normalised, nulled);
    }

    // Probes each expected scope by exact key (no ALLOW FILTERING); N = keyspaces * tables.
    private static async Task<Dictionary<string, string>> LoadAppliedFixupScopesAsync(
        ISession session,
        string[] keyspaces,
        CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var keyspace in keyspaces)
        {
            foreach (var table in ColorTables)
            {
                var scope = $"hex_color_fixup:{keyspace}:{table.Name}";
                var stmt = new SimpleStatement(
                    $"SELECT checksum FROM {LedgerTableFqn} WHERE scope = ? AND version = ?",
                    scope, FixupVersion);
                var rs = await session.ExecuteAsync(stmt);
                var row = rs.FirstOrDefault();
                if (row is not null)
                {
                    applied[scope] = row.GetValue<string>("checksum") ?? string.Empty;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return applied;
    }

    // IF NOT EXISTS keeps this race-safe against concurrent boots.
    private static async Task RecordFixupAsync(
        ISession session,
        string scope,
        CancellationToken cancellationToken)
    {
        var stmt = new SimpleStatement(
            $"INSERT INTO {LedgerTableFqn} (scope, version, checksum, applied_at, duration_ms, applied_by) " +
            "VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS",
            scope, FixupVersion, FixupChecksum, DateTimeOffset.UtcNow, 0, "hex-color-fixup");
        await session.ExecuteAsync(stmt);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed record ColorTable(string Name, string[] PrimaryKeyColumns);
}
