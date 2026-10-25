namespace Interfold.IntegrationTests.Shared.TestServices.TestBench;

/// <summary>Connection information returned by <see cref="TestBenchCoordinator.EnsureRunningAsync"/>
/// so callers can attach to the running test bench.</summary>
public sealed record BenchConnectionInfo(
    string LeaseFilePath);
