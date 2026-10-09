namespace Interfold.Shared.Contracts.Configuration;

/// <summary>
/// Testing environment configuration for integration test gating and connections.
/// Binds from environment variables with OCTOCON_RUN_* and OCTOCON_TEST_* prefixes.
/// </summary>
public sealed class TestingConfiguration
{
    public const string SectionName = "Octocon:Testing";

    /// <summary>
    /// When true, API integration tests (in-process API harness) will run.
    /// Env: OCTOCON_RUN_API_INTEGRATION
    /// </summary>
    public bool RunApiIntegration { get; init; }

    /// <summary>
    /// When true, live integration tests (real database connections) will run.
    /// Env: OCTOCON_RUN_LIVE_INTEGRATION
    /// </summary>
    public bool RunLiveIntegration { get; init; }
}
