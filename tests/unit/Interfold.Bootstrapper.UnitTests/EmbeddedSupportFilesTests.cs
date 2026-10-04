using System.Runtime.InteropServices;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Validates the embed-with-override contract: open streams from assembly resources and
/// materialize to disk only when a host path is required.
/// </summary>
public sealed class EmbeddedSupportFilesTests
{
    private const string ResourcePrefix = EmbeddedSupportFiles.ResourcePrefix;

    private static BootstrapOptions OptionsFor() => TestSupport.MakeOptions(
        command: BootstrapCommand.Bootstrap,
        outputDir: Path.GetTempPath(),
        skipPrereqs: true,
        nonInteractive: true);

    private static IReadOnlyList<string> EnumerateSupportResources() =>
        EmbeddedSupportFiles.EnumerateSupportResourceNames();

    private static byte[] ReadResourceBytes(string resourceName)
    {
        using var stream = typeof(EmbeddedSupportFiles).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Manifest resource '{resourceName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Test]
    public async Task OpensEveryEmbeddedSupportResource()
    {
        var resources = EnumerateSupportResources();
        await Assert.That(resources.Count).IsGreaterThanOrEqualTo(5)
            .Because("at least the embedded support files should still ship");

        foreach (var name in resources)
        {
            var relative = name[ResourcePrefix.Length..];
            using var stream = EmbeddedSupportFiles.Open(relative);
            await Assert.That(stream.CanRead).IsTrue()
                .Because($"resource '{name}' should open for read");
        }
    }


    [Test]
    public async Task MaterializeUnderSupportRootResolvesUnderOutputDir()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        var path = EmbeddedSupportFiles.MaterializeUnderSupportRoot(
            scratch.Path, EmbeddedSupportFiles.EdgeProxyParamsRelative);

        await Assert.That(path.StartsWith(EmbeddedSupportFiles.SupportRoot(scratch.Path))).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task SetsExecutableBitOnShellScriptsWhenMaterializedOnUnix()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new SkipTestException("Executable-bit assertion is Unix-only.");
        }

        using var scratch = TestSupport.NewScratchDir("interfold-embed");
        const string relative = "scripts/docker/ensure-host-aio.sh";
        var target = Path.Combine(scratch.Path, "ensure-host-aio.sh");
        EmbeddedSupportFiles.Materialize(relative, target, new PhaseLogger(OptionsFor()));

        var mode = File.GetUnixFileMode(target);
        await Assert.That(mode.HasFlag(UnixFileMode.UserExecute)).IsTrue();
    }

    [Test]
    public async Task NginxProxyParamsForwardsAccessJwtAndCloudflareProto()
    {
        using var stream = EmbeddedSupportFiles.Open(EmbeddedSupportFiles.EdgeProxyParamsRelative);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        await Assert.That(text).Contains("Cf-Access-Jwt-Assertion");
        await Assert.That(text).Contains("Cf-Access-Authenticated-User-Email");
        await Assert.That(text).Contains("$interfold_forwarded_proto");
    }

}
