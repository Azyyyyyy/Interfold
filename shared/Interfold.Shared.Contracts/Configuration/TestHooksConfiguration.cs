namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Gates test-only HTTP hooks (FCM outbox). Env: OCTOCON_TEST_HOOKS=1.</summary>
public sealed class TestHooksConfiguration
{
    public const string SectionName = "Octocon:TestHooks";

    public bool Enabled { get; set; }
}
