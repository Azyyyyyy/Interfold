namespace Interfold.StackMove;

internal static class StackMover
{
    public static async Task<int> ExecuteAsync(MoveRequest request, TextWriter output, CancellationToken cancellationToken)
    {
        if (request.From == request.To)
        {
            throw new MoveRefusedException(
                $"Source and target are both {request.From.ToString().ToLowerInvariant()}. " +
                "Use interfold-bootstrap backup and restore to copy a stack onto new hardware.");
        }

        var snapshot = await ReadSourceAsync(request, cancellationToken).ConfigureAwait(false);
        if (StackKindParser.IsCql(request.From))
        {
            RegionGuard.EnsureSingleRegion(snapshot.Regions);
        }

        var lossy = LossyScan.Evaluate(snapshot, request.To);
        if (lossy.Any && !request.AllowLossy)
        {
            var counts = MoveCounts.From(snapshot, SecretPolicy.Select(snapshot.Secrets, request.To).Count);
            await output.WriteLineAsync(BootstrapReport.Render(request, counts, lossy, wrote: false))
                .ConfigureAwait(false);
            await output.WriteLineAsync("Refusing the move because the target would drop values. Re-run with --allow-lossy to continue.")
                .ConfigureAwait(false);
            return 1;
        }

        var secrets = SecretPolicy.Select(snapshot.Secrets, request.To);
        var writtenCounts = MoveCounts.From(snapshot, secrets.Count);
        if (!request.DryRun)
        {
            await EnsureTargetEmptyAsync(request, cancellationToken).ConfigureAwait(false);
            await WriteTargetAsync(request, snapshot, secrets, cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync(BootstrapReport.Render(request, writtenCounts, lossy, wrote: !request.DryRun))
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task<StackSnapshot> ReadSourceAsync(MoveRequest request, CancellationToken cancellationToken)
    {
        if (request.From == StackKind.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(request.SourceSqlitePath))
            {
                throw new MoveRefusedException("Pass --sqlite or --source-sqlite for the source database file.");
            }

            if (!File.Exists(request.SourceSqlitePath))
            {
                throw new MoveRefusedException($"Source SQLite database was not found: {request.SourceSqlitePath}");
            }

            return await new SqliteStackStore(request.SourceSqlitePath).ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (request.SourceCql is null)
        {
            throw new MoveRefusedException("Pass --postgres and --cql-contact-points for the source cluster.");
        }

        return await new CqlStackStore(request.SourceCql).ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTargetEmptyAsync(MoveRequest request, CancellationToken cancellationToken)
    {
        if (request.To == StackKind.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(request.TargetSqlitePath))
            {
                throw new MoveRefusedException("Pass --sqlite or --target-sqlite for the target database file.");
            }

            if (PathsEqual(request.SourceSqlitePath, request.TargetSqlitePath))
            {
                throw new MoveRefusedException("Source and target SQLite paths are the same file.");
            }

            var count = await new SqliteStackStore(request.TargetSqlitePath).CountAccountsAsync(cancellationToken)
                .ConfigureAwait(false);
            PopulationGuard.EnsureEmpty(count, "SQLite database");
            return;
        }

        if (request.TargetCql is null)
        {
            throw new MoveRefusedException("Pass --postgres and --cql-contact-points for the target cluster.");
        }

        if (SameCql(request.SourceCql, request.TargetCql))
        {
            throw new MoveRefusedException("Source and target CQL endpoints are the same cluster and keyspace.");
        }

        var population = await new CqlStackStore(request.TargetCql).CountPopulationAsync(cancellationToken)
            .ConfigureAwait(false);
        PopulationGuard.EnsureEmpty(population, "CQL keyspace or global.user_registry");
    }

    private static async Task WriteTargetAsync(
        MoveRequest request,
        StackSnapshot snapshot,
        IReadOnlyList<SecretRow> secrets,
        CancellationToken cancellationToken)
    {
        if (request.To == StackKind.Sqlite)
        {
            await new SqliteStackStore(request.TargetSqlitePath!).WriteAsync(snapshot, secrets, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await new CqlStackStore(request.TargetCql!).WriteAsync(snapshot, secrets, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameCql(CqlEndpoint? left, CqlEndpoint? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.ContactPoints.Trim(), right.ContactPoints.Trim(), StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && string.Equals(left.Keyspace, right.Keyspace, StringComparison.OrdinalIgnoreCase);
    }
}
