using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.UnitTests.Attributes;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.UnitTests;

[RequiresWindows]
[SupportedOSPlatform("windows")]
public sealed class UnixFilePermissionsWindowsTests
{
    [Test]
    public async Task SetOwnerOnlyRemovesWorldAccess()
    {
        using var scratch = TestSupport.NewScratchDir("acl-0600");
        var path = Path.Combine(scratch.Path, "secret.key");
        await File.WriteAllTextAsync(path, "x");
        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: scratch.Path));

        UnixFilePermissions.SetOwnerOnly(path, logger);

        await Assert.That(HasWorldAllow(path)).IsFalse();
        await Assert.That(HasCurrentUserFullControl(path)).IsTrue();
    }

    [Test]
    public async Task SetWorldReadableGrantsWorldReadAndExecute()
    {
        using var scratch = TestSupport.NewScratchDir("acl-0644");
        var path = Path.Combine(scratch.Path, "cert.crt");
        await File.WriteAllTextAsync(path, "x");
        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: scratch.Path));

        UnixFilePermissions.SetWorldReadable(path, logger);

        await Assert.That(HasWorldAllow(path, FileSystemRights.ReadAndExecute)).IsTrue();
        await Assert.That(HasWorldAllow(path, FileSystemRights.Write)).IsFalse();
        await Assert.That(HasCurrentUserFullControl(path)).IsTrue();
    }

    [Test]
    public async Task SetWorldWritableGrantsWorldFullControlOnDirectory()
    {
        using var scratch = TestSupport.NewScratchDir("acl-0777");
        var path = Path.Combine(scratch.Path, "avatars");
        Directory.CreateDirectory(path);
        var logger = new PhaseLogger(TestSupport.MakeOptions(outputDir: scratch.Path));

        UnixFilePermissions.SetWorldWritable(path, logger);

        await Assert.That(HasWorldAllow(path, FileSystemRights.FullControl)).IsTrue();
        await Assert.That(HasCurrentUserFullControl(path)).IsTrue();
    }

    private static bool HasCurrentUserFullControl(string path)
    {
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("current Windows identity has no User SID");
        return GetAllowRules(path).Any(r =>
            r.IdentityReference.Equals(identity) &&
            (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }

    private static bool HasWorldAllow(string path, FileSystemRights? required = null)
    {
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        return GetAllowRules(path).Any(r =>
            r.IdentityReference.Equals(world) &&
            (required is null || (r.FileSystemRights & required.Value) == required.Value));
    }

    private static IEnumerable<FileSystemAccessRule> GetAllowRules(string path)
    {
        AuthorizationRuleCollection rules;
        if (Directory.Exists(path))
        {
            rules = new DirectoryInfo(path).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        }
        else
        {
            rules = new FileInfo(path).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        }

        return rules.Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow);
    }
}
