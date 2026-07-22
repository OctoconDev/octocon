using System.Reflection;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

// Pins that no PII-redacting wrapper (Category B) in Interfold.Contracts.Ids ships an
// `implicit operator string`. Adding one silently defeats SecretRedaction under any
// Foo(string) overload; interim guardrail until a Roslyn analyzer catches it at PR time.
public sealed class PiiWrapperImplicitWidenAbsenceTests
{
    [Test]
    [Arguments(typeof(DiscordId))]
    [Arguments(typeof(Email))]
    [Arguments(typeof(AppleId))]
    [Arguments(typeof(LinkToken))]
    [Arguments(typeof(PushToken))]
    [Arguments(typeof(ImportToken))]
    [Arguments(typeof(RecoveryCode))]
    [Arguments(typeof(Jti))]
    [Arguments(typeof(SocketToken))]
    [Arguments(typeof(EncryptionKeyMaterial))]
    [Arguments(typeof(KeyChecksum))]
    [Arguments(typeof(EncryptionSalt))]
    public async Task CategoryB_HasNoImplicitOperatorToString(Type wrapperType)
    {
        var implicitToString = wrapperType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit" && m.ReturnType == typeof(string))
            .ToArray();

        await Assert.That(implicitToString.Length).IsEqualTo(0)
            .Because($"{wrapperType.Name} is a PII-redacting wrapper and must NOT ship an implicit widen to string — that would silently defeat SecretRedaction under any Foo(string) overload. If you need the raw value at a persistence / DB / HTTP boundary, unwrap via .Value explicitly. See DiscordId's operator xml-doc for the full rationale.");
    }
}
