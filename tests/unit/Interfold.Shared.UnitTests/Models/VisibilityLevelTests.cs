using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Api.UnitTests.Models;

public sealed class VisibilityLevelTests
{
    [Test]
    [Arguments(null)]
    [Arguments((short)4)]
    [Arguments((short)-1)]
    public async Task FromStorage_NullOrUnknown_ReturnsPrivate(short? code)
    {
        await Assert.That(code.FromStorage()).IsEqualTo(VisibilityLevel.Private);
    }

    [Test]
    [Arguments((short)0, VisibilityLevel.Public)]
    [Arguments((short)1, VisibilityLevel.FriendsOnly)]
    [Arguments((short)2, VisibilityLevel.TrustedOnly)]
    [Arguments((short)3, VisibilityLevel.Private)]
    public async Task FromStorage_KnownCode_ReturnsMatchingLevel(short code, VisibilityLevel expected)
    {
        await Assert.That(code.FromStorage()).IsEqualTo(expected);
    }
}
