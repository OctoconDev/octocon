using System.Reflection;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.Ids;

/// <summary>
/// Guardrail: pins that no PII-redacting wrapper (Category B) in
/// <c>Interfold.Contracts.Ids</c> ships an <c>implicit operator string</c>. Adding one — even
/// "as a convenience" — silently defeats <see cref="SecretRedaction"/> under any
/// <c>Foo(string)</c> overload, and there is no behavioural test that would catch it because
/// the leak IS the absence of a behaviour.
///
/// <para>
/// Interim mechanism only. The long-term fix is a Roslyn analyzer that flags
/// <c>implicit operator string</c> on any type routing through <c>SecretRedaction</c>; until
/// that exists, this reflection sweep is what fires at PR-review time.
/// </para>
///
/// <para>
/// Category A operator behaviours (explicit narrow + implicit widen for
/// <see cref="SystemId"/>, <see cref="TagId"/>, <see cref="AlterId"/>, etc.) are deliberately
/// not covered here — the operator bodies are one-line forwards to the ctor and
/// <c>Value</c> property, and every consuming test naturally exercises them once the
/// call-site sweep lands.
/// </para>
/// </summary>
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
        // Reflection-pinned ABSENCE. If someone adds `implicit operator string(W)` on any of
        // these types, this test fires with the wrapper name in the failure message so the
        // reviewer sees which type reopened the leak and why it was blocked in the first
        // place. See DiscordId's operator xml-doc for the design rationale.
        var implicitToString = wrapperType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit" && m.ReturnType == typeof(string))
            .ToArray();

        await Assert.That(implicitToString.Length).IsEqualTo(0)
            .Because($"{wrapperType.Name} is a PII-redacting wrapper and must NOT ship an implicit widen to string — that would silently defeat SecretRedaction under any Foo(string) overload. If you need the raw value at a persistence / DB / HTTP boundary, unwrap via .Value explicitly. See DiscordId's operator xml-doc for the full rationale.");
    }
}
