using Interfold.Api.Services.SimplyPlural;

namespace Interfold.IntegrationTests.SimplyPluralImport;

/// <summary>
/// Unit coverage for <see cref="SpObjectId.TryDecodeTimestamp"/>. SP gives the importer no
/// `lastOperationTime` (or any time field) on custom fields - the only created-date signal
/// is the MongoDB ObjectId's first 4 bytes (big-endian Unix seconds). If we misdecode that we
/// either stamp the wrong date or accidentally accept garbage ids, so this nails down the
/// happy path plus the obvious failure modes. All inputs are synthetic.
/// </summary>
public sealed class SpObjectIdTests : BaseEndpointTest
{
    // 0x671425cd = 1,729,373,645 seconds since Unix epoch = 2024-10-19 21:34:05Z.
    // The remaining 16 hex chars are arbitrary - they're machine/process/counter bytes
    // that we don't care about for the timestamp decode.
    [Test]
    public async Task TryDecode_GoldenVector_ReturnsExpectedUtc()
    {
        var ok = SpObjectId.TryDecodeTimestamp("671425cd02c63d13c59ba474", out var utc);

        using (Assert.Multiple())
        {
            await Assert.That(ok).IsTrue();
            await Assert.That(utc).IsEqualTo(new DateTime(2024, 10, 19, 21, 34, 5, DateTimeKind.Utc));
        }
    }

    [Test]
    public async Task TryDecode_AcceptsUppercaseHex()
    {
        var ok = SpObjectId.TryDecodeTimestamp("671425CD02C63D13C59BA474", out var utc);

        using (Assert.Multiple())
        {
            await Assert.That(ok).IsTrue();
            await Assert.That(utc).IsEqualTo(new DateTime(2024, 10, 19, 21, 34, 5, DateTimeKind.Utc));
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-an-objectid")]
    [Arguments("671425cd02c63d13c59ba47")]   // 23 chars
    [Arguments("671425cd02c63d13c59ba4744")] // 25 chars
    [Arguments("zz1425cd02c63d13c59ba474")]  // non-hex in the timestamp prefix
    [Arguments("671425cd02c63d13c59ba47z")]  // non-hex in the trailing bytes
    public async Task TryDecode_InvalidInputs_ReturnFalseAndDefaultUtc(string? input)
    {
        var ok = SpObjectId.TryDecodeTimestamp(input, out var utc);

        using (Assert.Multiple())
        {
            await Assert.That(ok).IsFalse();
            await Assert.That(utc).IsEqualTo(default(DateTime));
        }
    }
}
