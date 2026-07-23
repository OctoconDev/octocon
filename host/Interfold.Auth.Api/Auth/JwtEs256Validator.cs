using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Interfold.Api.Helpers;

namespace Interfold.Api.Auth;

public sealed class ValidationResult
{
    public bool IsValid { get; }
    public string? ErrorMessage { get; }
    public ClaimsPrincipal? Principal { get; }

    public ValidationResult(bool isValid, string? errorMessage = null, ClaimsPrincipal? principal = null)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
        Principal = principal;
    }

    public static ValidationResult Success(ClaimsPrincipal principal) => new(true, null, principal);
    public static ValidationResult Failure(string errorMessage) => new(false, errorMessage, null);
}

/// <summary>ES256 JWT validation (signature + aud + exp).</summary>
public static class JwtEs256Validator
{
    public static Task<ValidationResult> ValidateAsync(
        string token,
        string keyPem,
        string expectedAudience,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(ValidationResult.Failure("Token is empty."));

        if (string.IsNullOrWhiteSpace(keyPem))
            return Task.FromResult(ValidationResult.Failure("Verification key is empty."));

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
                return Task.FromResult(ValidationResult.Failure("Token is not a valid JWS compact token."));

            string headerJson;
            try
            {
                headerJson = Encoding.UTF8.GetString(parts[0].Base64UrlDecode());
            }
            catch (Exception ex)
            {
                return Task.FromResult(ValidationResult.Failure($"Failed to decode header: {ex.Message}"));
            }

            var header = JsonSerializer.Deserialize<JwsHeader>(headerJson);
            if (header is null || string.IsNullOrWhiteSpace(header.Alg))
            {
                return Task.FromResult(ValidationResult.Failure("Missing JWT algorithm."));
            }

            if (!string.Equals(header.Alg, JwsHeader.Es256, StringComparison.Ordinal))
            {
                return Task.FromResult(ValidationResult.Failure($"Algorithm '{header.Alg}' is not supported. Only ES256 is accepted."));
            }

            var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
            byte[] signatureBytes;
            try
            {
                signatureBytes = parts[2].Base64UrlDecode();
            }
            catch (Exception ex)
            {
                return Task.FromResult(ValidationResult.Failure($"Failed to decode signature: {ex.Message}"));
            }

            using var ecdsa = ECDsa.Create();
            try
            {
                var normalizedPem = PemUtil.NormalizePem(keyPem);
                ecdsa.ImportFromPem(normalizedPem.AsSpan());
            }
            catch (CryptographicException ex)
            {
                return Task.FromResult(ValidationResult.Failure($"Failed to import verification key: {ex.Message}"));
            }

            if (!ecdsa.VerifyData(
                signingInput,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return Task.FromResult(ValidationResult.Failure("Invalid JWT signature."));
            }

            string payloadJson;
            try
            {
                payloadJson = Encoding.UTF8.GetString(parts[1].Base64UrlDecode());
            }
            catch (Exception ex)
            {
                return Task.FromResult(ValidationResult.Failure($"Failed to decode payload: {ex.Message}"));
            }

            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("aud", out var audProp) || audProp.GetString() != expectedAudience)
            {
                return Task.FromResult(ValidationResult.Failure("Audience mismatch."));
            }

            if (root.TryGetProperty("exp", out var expProp))
            {
                if (expProp.TryGetInt64(out var expSeconds))
                {
                    var expTime = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
                    if (expTime < DateTimeOffset.UtcNow)
                    {
                        return Task.FromResult(ValidationResult.Failure("Token is expired."));
                    }
                }
                else
                {
                    return Task.FromResult(ValidationResult.Failure("Invalid expiration format."));
                }
            }
            else
            {
                return Task.FromResult(ValidationResult.Failure("Missing expiration claim."));
            }

            var claims = new List<Claim>();
            if (root.TryGetProperty("sub", out var subProp))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, subProp.GetString() ?? string.Empty));
            }
            if (root.TryGetProperty("role", out var roleProp))
            {
                claims.Add(new Claim(ClaimTypes.Role, roleProp.GetString() ?? string.Empty));
            }

            var identity = new ClaimsIdentity(claims, "Jwt", ClaimTypes.NameIdentifier, ClaimTypes.Role);
            var principal = new ClaimsPrincipal(identity);

            return Task.FromResult(ValidationResult.Success(principal));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ValidationResult.Failure($"Validation failed: {ex.Message}"));
        }
    }

    /// <summary>Signature-only validation across any of the provided keys. Throws
    /// <see cref="SecurityTokenInvalidSignatureException"/> when nothing matches.</summary>
    public static SecurityToken ValidateSignature(string token, string[] pems, bool useJsonWebToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SecurityTokenInvalidSignatureException("Token is empty.");
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new SecurityTokenInvalidSignatureException("Token is not a valid JWS compact token.");
        }

        string headerJson;
        try
        {
            headerJson = Encoding.UTF8.GetString(parts[0].Base64UrlDecode());
        }
        catch (Exception ex)
        {
            throw new SecurityTokenInvalidSignatureException($"Failed to decode header: {ex.Message}");
        }

        var header = JsonSerializer.Deserialize<JwsHeader>(headerJson);
        if (header is null || string.IsNullOrWhiteSpace(header.Alg))
        {
            throw new SecurityTokenInvalidSignatureException("Missing JWT algorithm.");
        }

        if (!string.Equals(header.Alg, JwsHeader.Es256, StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidSignatureException($"Algorithm '{header.Alg}' is not supported. Only ES256 is accepted.");
        }

        if (pems == null || pems.Length == 0)
        {
            throw new SecurityTokenInvalidSignatureException("No ES256 verification keys are configured.");
        }

        var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
        byte[] signatureBytes;
        try
        {
            signatureBytes = parts[2].Base64UrlDecode();
        }
        catch (Exception ex)
        {
            throw new SecurityTokenInvalidSignatureException($"Failed to decode signature: {ex.Message}");
        }

        int attemptCount = 0;
        foreach (var rawPem in pems)
        {
            attemptCount++;
            using var ecdsa = ECDsa.Create();
            try
            {
                var normalizedPem = PemUtil.NormalizePem(rawPem);
                ecdsa.ImportFromPem(normalizedPem.AsSpan());
            }
            catch (CryptographicException)
            {
                continue;
            }

            try
            {
                if (ecdsa.VerifyData(
                    signingInput,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    if (useJsonWebToken)
                    {
                        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);
                    }
                    else
                    {
                        return new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(token);
                    }
                }
            }
            catch (CryptographicException)
            {
                continue;
            }
        }

        throw new SecurityTokenInvalidSignatureException(
            $"Invalid JWT signature: tested {attemptCount} verification key(s) but none matched.");
    }
}
