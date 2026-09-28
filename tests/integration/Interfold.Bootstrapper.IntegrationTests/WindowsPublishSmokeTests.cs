using System.Diagnostics;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Thin native Windows <c>publish</c> smoke (no compose up / Scylla). Skips when
/// Docker Desktop is not reachable — GHA windows-latest often has no Linux engine;
/// local Docker Desktop still exercises the path.
/// </summary>
[RequiresWindows]
[Explicit]
public sealed class WindowsPublishSmokeTests
{
    [Test]
    public async Task PublishWritesComposeEnvAndCerts()
    {
        await EnsureDockerComposeOrSkipAsync();

        await using var scratch = await WindowsInstallScratch.CreateAsync(nameof(PublishWritesComposeEnvAndCerts));
        var result = await scratch.RunPublishAsync();
        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"publish failed: {result.Stderr}\n{result.Stdout}");

        await Assert.That(File.Exists(Path.Combine(scratch.OutputDir, "docker-compose.yaml"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(scratch.OutputDir, ".env"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(scratch.OutputDir, "certs", "rootCA.crt"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(scratch.OutputDir, "secrets", "secrets.json"))).IsTrue();
    }

    private static async Task EnsureDockerComposeOrSkipAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("compose");
            psi.ArgumentList.Add("version");

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start docker.");
            await proc.WaitForExitAsync().ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                throw new SkipTestException(
                    $"docker compose version exited {proc.ExitCode}; Docker Desktop not ready.");
            }
        }
        catch (SkipTestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SkipTestException(
                $"Docker not available for Windows publish smoke ({ex.GetType().Name}: {ex.Message}).");
        }
    }
}
