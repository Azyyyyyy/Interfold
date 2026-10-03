using Interfold.Bootstrapper.Configuration;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class BootstrapperApiImageTests
{
    [Test]
    public async Task DebugPinsDevelopAndReleasePinsStable()
    {
#if DEBUG
        await Assert.That(BootstrapperApiImage.Default).IsEqualTo(BootstrapperApiImage.BleedingEdge);
#else
        await Assert.That(BootstrapperApiImage.Default).IsEqualTo(DefaultContainerImages.Api);
#endif
        await Assert.That(new ApiSection().Image).IsEqualTo(BootstrapperApiImage.Default);
    }
}
