using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Interfold.Infrastructure.Sqlite;

public sealed class SqliteHealthChecker(ISqliteConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

            await using (var ping = connection.CreateCommand())
            {
                ping.CommandText = "SELECT 1";
                await ping.ExecuteScalarAsync(cancellationToken);
            }

            await using (var table = connection.CreateCommand())
            {
                table.CommandText =
                    "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'octocon_idempotency' LIMIT 1";
                var found = await table.ExecuteScalarAsync(cancellationToken);
                if (found is null)
                {
                    throw new InvalidOperationException(
                        "SQLite table 'octocon_idempotency' was not found. Run schema migrations first.");
                }
            }

            return new HealthCheckResult(HealthStatus.Healthy, "Connected and required table exists.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(HealthStatus.Unhealthy, exception: ex);
        }
    }
}
