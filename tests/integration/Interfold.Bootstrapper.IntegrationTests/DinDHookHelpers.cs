using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Shared body for the one-line <c>[After(Test)] DumpOnFailure</c> hook that every
/// DinD integration-test class carries.
/// </summary>
/// <remarks>
/// The 19 <c>[After(Test)] public Task DumpOnFailure(...)</c> hooks across this project
/// look ripe for a <c>DinDIntegrationTestBase&lt;TFixture&gt;</c> lift-to-base
/// refactor, but Round-1 C1 deliberately picked the per-class hook over inheritance
/// because the test classes use TUnit's primary-ctor fixture pattern
/// <c>(UbuntuDinDFixture dinD)</c> — the compiler-generated backing field can't be
/// re-exposed to a base class without either giving up primary-ctor syntax or
/// duplicating the fixture parameter on both ctor and base. Round-2 D10 re-evaluated
/// the shape and confirmed no drift (all 19 sites are still the byte-identical
/// single-line delegate to this helper, with 3 sites overriding <c>teardown: false</c>);
/// the per-class hook stays.
/// </remarks>
public static class DinDHookHelpers
{
    public static async Task DumpOnFailureAsync<TFixture>(TFixture dinD, TestContext ctx, bool teardown = true)
        where TFixture : DinDFixtureBase
    {
        if (ctx.Execution.Result?.State == TestState.Failed)
        {
            await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
        }

        if (teardown)
        {
            await dinD.TearDownComposeAsync(ctx.Metadata.TestName);
        }
    }
}

