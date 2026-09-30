namespace Interfold.StackMove;

public enum StackKind
{
    Sqlite,
    Scylla,
    Cassandra,
}

internal static class StackKindParser
{
    public static bool TryParse(string? raw, out StackKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "sqlite":
                kind = StackKind.Sqlite;
                return true;
            case "scylla":
                kind = StackKind.Scylla;
                return true;
            case "cassandra":
                kind = StackKind.Cassandra;
                return true;
            default:
                return false;
        }
    }

    public static bool IsCql(StackKind kind) => kind is StackKind.Scylla or StackKind.Cassandra;

    public static string CqlBackendWire(StackKind kind) => kind switch
    {
        StackKind.Scylla => "scylla-single",
        StackKind.Cassandra => "cassandra",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a CQL stack."),
    };
}
