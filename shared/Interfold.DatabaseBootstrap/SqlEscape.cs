namespace Interfold.DatabaseBootstrap;

/// <summary>Doubles apostrophes in SQL/CQL string literals. Both Postgres and
/// Cassandra follow the same rule. The bootstrapper's password alphabet excludes the
/// apostrophe, so this is defence-in-depth for operator-supplied values.</summary>
public static class SqlEscape
{
    public static string Literal(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
