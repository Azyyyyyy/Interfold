namespace Interfold.StackMove;

internal static class RegionGuard
{
    public static void EnsureSingleRegion(IReadOnlyList<string> regions)
    {
        var distinct = regions
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Select(region => region.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinct.Length <= 1)
        {
            return;
        }

        throw new MoveRefusedException(
            "The source user registry spans more than one keyspace (" +
            string.Join(", ", distinct) +
            "). This move keeps a single region. SQLite stores prefix-stripped ids, and a Scylla or Cassandra target is stamped with one keyspace.");
    }
}

internal static class PopulationGuard
{
    public static void EnsureEmpty(long population, string target)
    {
        if (population > 0)
        {
            throw new MoveRefusedException(
                $"Target {target} already has rows ({population}). Refusing to mix two populations.");
        }
    }
}
