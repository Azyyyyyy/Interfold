extern alias AppHost;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Interfold.IntegrationTests.Shared.TestServices.TestBench;
using Interfold.Shared.Contracts.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Session-shared Aspire host for DB-bound integration tests.
/// SQLite-oriented shell for DB-bound integration tests.</summary>
public sealed class SharedDbFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    private readonly bool _benchMode = TestBenchCoordinator.IsOptedIn();

    protected override string[] Args => BuildArgs();

    private static string[] BuildArgs()
    {
        LifecycleProbe.Log("SharedDbFixture.BuildArgs");
        return
        [
            $"{AppHostParameterKeys.Persistence}=sqlite",
            $"{AppHostParameterKeys.IncludeApi}=false",
            $"{AppHostParameterKeys.IncludeWeb}=false",
            $"{AppHostParameterKeys.PersistentContainers}=false",
            $"{AppHostParameterKeys.EncryptionPrivateKey}=TEST",
        ];
    }

    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(5);
    protected override bool EnableTelemetryCollection => false;
    protected override ResourceWaitBehavior WaitBehavior => ResourceWaitBehavior.None;

    public override async Task InitializeAsync()
    {
        if (_benchMode)
        {
            await InitializeBenchModeAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        AspireProcessEndpoints.ApplyToCurrentProcess(
            AspireProcessEndpoints.LegacySharedDbResourceService,
            AspireProcessEndpoints.LegacySharedDbOtlp,
            AspireProcessEndpoints.LegacySharedDbAppUrls);

        await DockerMemoryPreflight.EnsureAdequateAsync(CancellationToken.None).ConfigureAwait(false);
        await base.InitializeAsync().ConfigureAwait(false);
    }

    private async Task InitializeBenchModeAsync(CancellationToken cancellationToken)
    {
        LifecycleProbe.Log("SharedDbFixture.InitializeBenchMode");
        await TestBenchCoordinator.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        using var _ = LifecycleProbe.BeginTimed("AfterFixtureDispose:SharedDbFixture");
        LifecycleProbe.Log("BeforeFixtureDispose:SharedDbFixture");

        if (_benchMode)
        {
            await TestBenchCoordinator.DetachAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            await AspireContainerCleanup
                .StopResourcesAsync(AspireContainerCleanup.SharedDbResourceNames())
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort pre-stop; base dispose must still run.
        }

        await base.DisposeAsync().ConfigureAwait(false);

        var remaining = await AspireContainerCleanup.CountRunningAspireContainersAsync().ConfigureAwait(false);
        if (remaining > 0)
            LifecycleProbe.Log($"SharedDbFixture.RemainingAspireContainers:{remaining}");
    }

    protected override Task WaitForResourcesAsync(DistributedApplication app, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
