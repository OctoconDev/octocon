using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Settings.Contracts.Ids;
using Interfold.Shared.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace Interfold.Settings.Api.Helpers;

public static class RecoveryCodeResolver
{
    /// <summary>
    /// Decrypt a compact JWE recovery-code ciphertext and return the plaintext wrapped in
    /// a <see cref="RecoveryCode"/> whose <see cref="RecoveryCode.ToString"/> redacts.
    /// Wrapping inside the resolver keeps the plaintext as a bare <c>string</c> only
    /// inside <see cref="TryDecryptJwe"/> (no logging or interpolation surface); every
    /// external observation goes through the redacted <c>ToString</c>.
    /// </summary>
    public static bool TryResolve(string candidate, string privateKeyPem, out RecoveryCode recoveryCode, out ErrorCode errorCode)
    {
        recoveryCode = default;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            errorCode = ErrorCodes.RecoveryCodeNotProvided;
            return false;
        }
        if (!LooksLikeCompactJwe(candidate))
        {
            errorCode = ErrorCodes.RecoveryCodeNotJwe;
            return false;
        }

        if (!TryLoadEncryptionPrivateKey(ref privateKeyPem)
            || !TryDecryptJwe(candidate, privateKeyPem, out var plaintext))
        {
            errorCode = ErrorCodes.DecryptionError;
            return false;
        }

        errorCode = default;
        // Wrap-at-decrypt-boundary: the plaintext local dies with this method frame; every
        // downstream reference goes through the redacting RecoveryCode wrapper.
        recoveryCode = new(plaintext);
        return !string.IsNullOrWhiteSpace(plaintext);
    }

    public static bool LooksLikeCompactJwe(string token) => token.Count(ch => ch == '.') == 4;

    private static bool TryLoadEncryptionPrivateKey(ref string privateKeyPem)
    {
        privateKeyPem = privateKeyPem.Trim().Replace("\\n", "\n", StringComparison.Ordinal);
        return privateKeyPem.Contains("BEGIN", StringComparison.Ordinal);
    }

    private static bool TryDecryptJwe(string compactJwe, string privateKeyPem, out string plaintext)
    {
        plaintext = string.Empty;

        try
        {
            var parts = compactJwe.Split('.');
            if (parts.Length != 5)
                return false;

            var headerJson = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[0]));
            using var header = JsonDocument.Parse(headerJson);
            var alg = header.RootElement.TryGetProperty("alg", out var algProp) ? algProp.GetString() : null;
            var enc = header.RootElement.TryGetProperty("enc", out var encProp) ? encProp.GetString() : null;

            if (!string.Equals(alg, "RSA-OAEP-256", StringComparison.Ordinal)
                || !string.Equals(enc, "A256GCM", StringComparison.Ordinal))
            {
                return false;
            }

            var encryptedKey = WebEncoders.Base64UrlDecode(parts[1]);
            var iv = WebEncoders.Base64UrlDecode(parts[2]);
            var ciphertext = WebEncoders.Base64UrlDecode(parts[3]);
            var tag = WebEncoders.Base64UrlDecode(parts[4]);
            var aad = Encoding.ASCII.GetBytes(parts[0]);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var cek = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);

            var decrypted = new byte[ciphertext.Length];
            using var aes = new AesGcm(cek, tag.Length);
            aes.Decrypt(iv, ciphertext, tag, decrypted, aad);

            plaintext = Encoding.UTF8.GetString(decrypted);
            return !string.IsNullOrWhiteSpace(plaintext);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
