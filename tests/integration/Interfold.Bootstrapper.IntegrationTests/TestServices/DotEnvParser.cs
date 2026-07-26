using System.Text;

namespace Interfold.Bootstrapper.IntegrationTests.TestServices;

/// <summary>
/// Cheap-and-cheerful <c>.env</c> parser shared by publish-style integration tests. Lines of
/// the form <c>KEY=VALUE</c>, comments (lines beginning with <c>#</c>) stripped, blank lines
/// ignored. We don't bother handling quoted values because the bootstrapper's emitter doesn't
/// produce any (every value is either a known-safe alphabet password or an absolute path).
/// Previously duplicated verbatim in <c>PublishIntegrationTests</c> and <c>WebHttpsTests</c>.
/// </summary>
internal static class DotEnvParser
{
    public static IDictionary<string, string> ParseEnv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            dict[line[..eq]] = line[(eq + 1)..];
        }
        return dict;
    }
}
