using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla.Fixups;

/// <summary>
/// One-shot data fixup that normalises legacy <c>color</c> columns across every
/// colour-bearing table before <see cref="HexColor.FromNullable"/> starts throwing
/// on malformed values.
///
/// <para>
/// <b>Why this exists.</b> Prior to the strict <see cref="HexColor"/> contract, three
/// paths could write a non-well-formed colour into Scylla:
/// </para>
/// <list type="number">
///   <item><description>The Simply Plural importer historically stored bare six-char
///   hex (<c>FF0000</c>) without a leading <c>#</c>; the importer has since been
///   fixed to canonicalise via <see cref="HexColor.Normalise"/>, but existing rows
///   still carry the bare form.</description></item>
///   <item><description>The pre-strict <c>HexColorJsonConverter</c> accepted any
///   string, so any client bug that POSTed a garbage value landed verbatim in the
///   DB.</description></item>
///   <item><description>The pre-typed-HexColor era wrote raw text into the same
///   column.</description></item>
/// </list>
///
/// <para>
/// This service runs once per (keyspace, table) pair via a ledger row in
/// <c>global.schema_migrations</c> with scope <c>hex_color_fixup:{keyspace}:{table}</c>.
/// On a fresh cluster it's effectively a no-op (nothing to normalise) and still records
/// the ledger row so subsequent boots skip the scan.
/// </para>
///
/// <para>
/// <b>Invariant afterwards.</b> Every non-null <c>color</c> value across
/// <c>alters</c>, <c>tags</c>, <c>alter_journals</c>, <c>alter_journals_by_alter</c>,
/// and <c>global_journals</c> is well-formed by <see cref="HexColor.IsWellFormed"/> —
/// which is the precondition <see cref="HexColor.FromNullable"/> now assumes.
/// </para>
/// </summary>
public sealed class HexColorFixupService(
    IOptions<PersistenceConfiguration> options,
    IScyllaSessionProvider sessionProvider,
    IScyllaConfigResolver configResolver,
    ILogger<HexColorFixupService> logger) : IHostedLifecycleService
{
    private const string LedgerTableFqn = "global.schema_migrations";
    private const string FixupVersion = "v1";

    // Bump when the normalisation logic changes so previously-recorded fixup runs
    // are considered stale. Kept as a code-side sentinel rather than a file hash
    // because the fixup lives in this service (no separate .cql file to hash).
    private const string FixupChecksum = "hex_color_fixup_v1_normalise_bare_hex_or_null";

    // Tables that carry a `color` text column. All share the same normalisation
    // logic; only the PK columns differ. The `alter_journals_by_alter` table is
    // a lookup denormalisation of `alter_journals` and must be fixed up
    // independently so the two stay in sync.
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
        // The ScyllaMigrationService (registered first) is the source of truth for
        // whether Scylla is actually reachable and provisioned in this deployment.
        // If we can't get a session, log and bail — the persistence-not-configured
        // codepaths (in-memory tests, bootstrap without secrets) already log why.
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
        // Reasonable page size — large enough to make progress on a big table, small
        // enough to stay well under memory pressure while paging.
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
            updateArgs[0] = canonical; // may be null → sets color to NULL
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

    private static async Task<Dictionary<string, string>> LoadAppliedFixupScopesAsync(
        ISession session,
        string[] keyspaces,
        CancellationToken cancellationToken)
    {
        // We could pull the entire ledger, but scoping the read to the fixup rows
        // keeps this from stomping on unrelated migration entries when the ledger
        // grows. There's no direct "scope LIKE 'hex_color_fixup:%'" support without
        // ALLOW FILTERING, so we probe each expected scope by exact key and populate
        // the map. The N here is O(keyspaces * tables) — small.
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

    private static async Task RecordFixupAsync(
        ISession session,
        string scope,
        CancellationToken cancellationToken)
    {
        // Uses IF NOT EXISTS to be race-safe against concurrent boots — matches the
        // pattern in ScyllaMigrationService.RecordMigrationAsync.
        var stmt = new SimpleStatement(
            $"INSERT INTO {LedgerTableFqn} (scope, version, checksum, applied_at, duration_ms, applied_by) " +
            "VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS",
            scope, FixupVersion, FixupChecksum, DateTimeOffset.UtcNow, 0, "hex-color-fixup");
        await session.ExecuteAsync(stmt);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed record ColorTable(string Name, string[] PrimaryKeyColumns);
}
