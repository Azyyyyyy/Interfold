using System.Net.Sockets;
using Cassandra;
using Npgsql;

namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Cold-start driver-side wait loops used by the in-process seed callers
/// (<c>Interfold.AppHost</c>'s dev seed hosted service and the TUnit.Aspire fixtures).
/// The bootstrapper's own compose-exec seed uses different probes because "healthy" for a
/// container-exec differs from "healthy" for an out-of-container Npgsql/DataStax connection.
/// </summary>
public static class InProcessSeedWaits
{
    /// <summary>Built-in Scylla / Cassandra superuser present before <see cref="ScyllaSeeder"/> locks it.</summary>
    public const string ScyllaDefaultUser = "cassandra";

    /// <summary>Built-in password for <see cref="ScyllaDefaultUser"/>.</summary>
    public const string ScyllaDefaultPassword = "cassandra";

    /// <summary>
    /// Waits until the caller's init role can complete <c>SELECT 1</c> against Postgres.
    /// Requires <paramref name="options"/>.<c>RequiredConsecutiveSuccesses</c> back-to-back
    /// successes so we don't return during the socket-open-but-still-crash-recovering window
    /// that fresh TimescaleDB starts hit.
    /// </summary>
    public static async Task WaitForPostgresAsync(
        string initConnectionString,
        PostgresReadinessOptions options,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(options.Timeout);
        var attempt = 0;
        var consecutiveSuccesses = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(initConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (result is int one && one == 1)
                {
                    consecutiveSuccesses++;
                    if (consecutiveSuccesses >= options.RequiredConsecutiveSuccesses) return;
                }
                else if (consecutiveSuccesses > 0)
                {
                    consecutiveSuccesses = 0;
                }
            }
            // Broad catch by driver exception type: any transient socket / auth / timeout
            // failure resets the streak; the deadline is the real timeout.
            catch (NpgsqlException) { consecutiveSuccesses = 0; }
            catch (SocketException) { consecutiveSuccesses = 0; }
            catch (TimeoutException) { consecutiveSuccesses = 0; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Postgres did not become ready within {options.Timeout.TotalMinutes} minutes ({attempt} probes).");
    }

    /// <summary>
    /// Waits until any of the candidate CQL accounts answers a <c>system.local</c> query.
    /// Always tries the built-in <c>cassandra</c> pair first (fresh volume); <paramref name="extraCredentials"/>
    /// covers persistent reruns after <see cref="ScyllaSeeder"/> has locked that account.
    /// </summary>
    public static Task WaitForScyllaAsync(string host, int port, CancellationToken ct)
        => WaitForScyllaAsync(host, port, extraCredentials: null, logger: null, ct);

    /// <inheritdoc cref="WaitForScyllaAsync(string, int, CancellationToken)"/>
    public static async Task WaitForScyllaAsync(
        string host,
        int port,
        IReadOnlyList<(string User, string Password)>? extraCredentials,
        IDatabaseInitLogger? logger,
        CancellationToken ct)
    {
        var candidates = BuildCqlWaitCandidates(extraCredentials);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            foreach (var (user, password) in candidates)
            {
                try
                {
                    using var cluster = DataStaxScyllaExecutor.CreateCluster(host, port, user, password);
                    using var session = await cluster.ConnectAsync()
                        .WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                    var rs = await session.ExecuteAsync(new SimpleStatement("SELECT cluster_name FROM system.local"))
                        .WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                    if (rs.GetRows().Any())
                    {
                        logger?.Info($"    cql ready as '{user}' after {attempt} attempt(s)");
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (DateTime.UtcNow < deadline)
                {
                    logger?.Info(
                        $"    cql wait attempt {attempt} as '{user}': {ex.GetType().Name}: {OneLine(ex.Message)}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"scylla/cassandra at {host}:{port} did not become ready within 5 minutes ({attempt} probes).");
    }

    private static List<(string User, string Password)> BuildCqlWaitCandidates(
        IReadOnlyList<(string User, string Password)>? extraCredentials)
    {
        var candidates = new List<(string User, string Password)>
        {
            (ScyllaDefaultUser, ScyllaDefaultPassword),
        };
        if (extraCredentials is null) return candidates;

        foreach (var (user, password) in extraCredentials)
        {
            if (string.IsNullOrWhiteSpace(user)) continue;
            if (string.Equals(user, ScyllaDefaultUser, StringComparison.Ordinal)) continue;
            candidates.Add((user, password));
        }
        return candidates;
    }

    private static string OneLine(string message)
    {
        var trimmed = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }
}
