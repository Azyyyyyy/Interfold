namespace Interfold.Bootstrapper.UnitTests.Attributes;

/// <summary>
/// Skips the test (class or method) on non-Windows hosts. Prefer this over
/// silent <c>if (!OperatingSystem.IsWindows()) return;</c> so skipped coverage
/// shows up in the runner instead of a green no-op.
/// </summary>
public sealed class RequiresWindowsAttribute()
    : SkipAttribute("Native Windows bootstrapper tests require a Windows host")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(!OperatingSystem.IsWindows());
}
