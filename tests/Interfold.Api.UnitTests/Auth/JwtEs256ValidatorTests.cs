using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Interfold.Api.Auth;
using TUnit.Core;

namespace Interfold.Api.UnitTests.Auth;

public sealed class JwtEs256ValidatorTests
{
    private static (string Token, string PrivateKeyPem, string PublicKeyPem) GenerateToken(
        string sub = "user123",
        string aud = "octocon",
        long? exp = null,
        bool modifySignature = false,
        string? alg = "ES256")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var headerObj = alg != null ? new { alg } : null;
        var header = headerObj != null ? JsonSerializer.Serialize(headerObj) : "{}";
        
        var expValue = exp ?? DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new { sub, aud, exp = expValue });

        var headerB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(header)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signingInput = headerB64 + "." + payloadB64;

        var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (modifySignature)
        {
            signature[0] ^= 0xFF;
        }

        var signatureB64 = Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = signingInput + "." + signatureB64;

        return (token, privateKeyPem, publicKeyPem);
    }

    [Test]
    public async Task ValidateAsync_ValidToken_ReturnsSuccess()
    {
        var (token, _, publicKeyPem) = GenerateToken();

        var result = await JwtEs256Validator.ValidateAsync(token, publicKeyPem, "octocon");

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.ErrorMessage).IsNull();
        await Assert.That(result.Principal).IsNotNull();
        await Assert.That(result.Principal!.Identity!.Name).IsEqualTo("user123");
    }

    [Test]
    public async Task ValidateAsync_ExpiredToken_ReturnsFailure()
    {
        var expiredTime = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var (token, _, publicKeyPem) = GenerateToken(exp: expiredTime);

        var result = await JwtEs256Validator.ValidateAsync(token, publicKeyPem, "octocon");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.ErrorMessage).Contains("expired");
    }

    [Test]
    public async Task ValidateAsync_AudienceMismatch_ReturnsFailure()
    {
        var (token, _, publicKeyPem) = GenerateToken(aud: "wrong-audience");

        var result = await JwtEs256Validator.ValidateAsync(token, publicKeyPem, "octocon");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.ErrorMessage).Contains("Audience mismatch");
    }

    [Test]
    public async Task ValidateAsync_InvalidSignature_ReturnsFailure()
    {
        var (token, _, publicKeyPem) = GenerateToken(modifySignature: true);

        var result = await JwtEs256Validator.ValidateAsync(token, publicKeyPem, "octocon");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.ErrorMessage).Contains("Invalid JWT signature");
    }

    [Test]
    public async Task ValidateAsync_NonEs256Algorithm_ReturnsFailure()
    {
        var (token, _, publicKeyPem) = GenerateToken(alg: "RS256");

        var result = await JwtEs256Validator.ValidateAsync(token, publicKeyPem, "octocon");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.ErrorMessage).Contains("Only ES256 is accepted");
    }

    [Test]
    public async Task ValidateAsync_PemNormalization_AcceptsVariousNewlines()
    {
        var (token, _, publicKeyPem) = GenerateToken();
        var messyPem = publicKeyPem.Replace("\n", "\\r\\n");

        var result = await JwtEs256Validator.ValidateAsync(token, messyPem, "octocon");

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ValidateSignature_ValidSignature_ReturnsToken()
    {
        var (token, _, publicKeyPem) = GenerateToken();

        var tokenObj = JwtEs256Validator.ValidateSignature(token, [publicKeyPem], useJsonWebToken: true);

        await Assert.That(tokenObj).IsNotNull();
    }

    [Test]
    public async Task ValidateSignature_InvalidSignature_Throws()
    {
        var (token, _, publicKeyPem) = GenerateToken(modifySignature: true);

        await Assert.That(() => JwtEs256Validator.ValidateSignature(token, [publicKeyPem], useJsonWebToken: true))
            .Throws<Microsoft.IdentityModel.Tokens.SecurityTokenInvalidSignatureException>();
    }
}
