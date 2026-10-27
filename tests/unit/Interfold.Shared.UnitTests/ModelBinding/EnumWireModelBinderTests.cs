using Interfold.Shared.Contracts.Enums;

namespace Interfold.Api.UnitTests.ModelBinding;

public sealed class WireEnumParseTests
{
    [Test]
    public async Task Parse_WireValue_BindsSuccessfully()
    {
        var ok = WireEnum<AvatarSource>.TryParse("local", null, out var parsed);

        await Assert.That(ok).IsTrue();
        await Assert.That(parsed.Value).IsEqualTo(AvatarSource.Local);
    }

    [Test]
    public async Task Parse_MemberName_BindsSuccessfully()
    {
        var ok = WireEnum<AvatarSource>.TryParse("Local", null, out var parsed);

        await Assert.That(ok).IsTrue();
        await Assert.That(parsed.Value).IsEqualTo(AvatarSource.Local);
    }

    [Test]
    public async Task Parse_UnknownValue_ReturnsFalse()
    {
        var ok = WireEnum<AvatarSource>.TryParse("nope", null, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Parse_Blank_ReturnsFalse()
    {
        await Assert.That(WireEnum<AvatarSource>.TryParse("  ", null, out _)).IsFalse();
        await Assert.That(WireEnum<AvatarSource>.TryParse(null, null, out _)).IsFalse();
    }
}
