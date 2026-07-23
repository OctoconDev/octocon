using System;

namespace Interfold.Api.Auth;

/// <summary>
/// Utility methods for handling PEM format keys.
/// </summary>
public static class PemUtil
{
    /// <summary>
    /// PEMs arrive from env vars / DB with both real and escaped line endings; collapse all
    /// of them to '\n' so ECDsa.ImportFromPem accepts the result.
    /// </summary>
    public static string NormalizePem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
            return pem;

        // PEMs arrive from env vars / DB with both real and escaped line endings; collapse all
        // of them to '\n' so ECDsa.ImportFromPem accepts the result.
        var normalized = pem
            .Replace(@"\r\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        return normalized;
    }
}
