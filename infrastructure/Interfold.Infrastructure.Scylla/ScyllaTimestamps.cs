namespace Interfold.Infrastructure.Scylla;

// Unspecified DateTime Kind → UTC for CQL timestamp binds (matches Sqlite unix-ms path).
// Driver codec takes DateTimeOffset; wire is unix-ms. Raw long needs a custom codec.
internal static class ScyllaTimestamps
{
    public static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    public static DateTimeOffset? AsUtc(DateTime? value)
        => value is { } v ? AsUtc(v) : null;
}
