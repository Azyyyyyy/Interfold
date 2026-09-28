namespace Interfold.Bootstrapper.IntegrationTests.Attributes;

/// <summary>
/// Skips the test class on non-Windows hosts. Used by native Windows
/// bootstrapper coverage (Task Scheduler install-service, publish smoke) so the
/// Linux DinD matrix can keep a <c>/*/*/*/*</c> filter without failing on those
/// methods.
/// </summary>
public sealed class RequiresWindowsAttribute()
    : SkipAttribute("Native Windows bootstrapper tests require a Windows host")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(!OperatingSystem.IsWindows());
}
