namespace Interfold.StackMove;

internal static class UnixMs
{
    public static long From(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static long? From(DateTimeOffset? value) =>
        value is { } present ? present.ToUnixTimeMilliseconds() : null;

    public static DateTimeOffset ToOffset(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

    public static DateTimeOffset? ToOffset(long? unixMs) =>
        unixMs is { } present ? DateTimeOffset.FromUnixTimeMilliseconds(present) : null;
}

internal static class GuidText
{
    public static string Format(Guid id) => id.ToString("N");

    public static Guid Parse(string text)
    {
        if (Guid.TryParseExact(text, "N", out var exact))
        {
            return exact;
        }

        return Guid.Parse(text);
    }
}

internal readonly record struct FriendshipCompanion(string FriendId, string UserId, short Level, long SinceUnixMs)
{
    public static FriendshipCompanion For(FriendshipRow row) => new(
        SystemIds.Bare(row.FriendId),
        SystemIds.Bare(row.UserId),
        row.Level,
        row.SinceUnixMs);
}
