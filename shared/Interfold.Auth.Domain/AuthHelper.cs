using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Auth.Domain;

public class AuthHelper
{
    public static string CreateToken(AuthenticationConfiguration authConfig, DateTimeOffset expiresAt, DateTimeOffset now, Jti jti, SystemId systemId)
    {
        var headerJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "ES256",
            ["typ"] = "JWT"
        });

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = authConfig.JwtAuthority,
            ["sub"] = systemId.Value,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["jti"] = jti.Value,
            ["scope"] = "octocon:deeplink"
        });

        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(NormalizePem(authConfig.JwtEs256PrivateKeyPem).AsSpan());
        var signature = ecdsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var encodedSignature = Base64UrlEncode(signature);

        var token = $"{signingInput}.{encodedSignature}";
        return token;
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string NormalizePem(string pem)
        => pem.Replace("\\n", "\n", StringComparison.Ordinal);
}
