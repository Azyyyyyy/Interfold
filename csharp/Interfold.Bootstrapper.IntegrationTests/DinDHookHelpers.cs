using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

public static class DinDHookHelpers
{
    public static async Task DumpOnFailureAsync<TFixture>(TFixture dinD, TestContext ctx, bool teardown = true)
        where TFixture : DinDFixtureBase
    {
        if (ctx.Execution.Result?.State == TestState.Failed)
        {
            await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
        }

        if (teardown)
        {
            await dinD.TearDownComposeAsync(ctx.Metadata.TestName);
        }
    }
}

