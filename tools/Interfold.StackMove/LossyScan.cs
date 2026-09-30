namespace Interfold.StackMove;

internal sealed record LossyCounts(
    int GoogleIds,
    int ExtraImages,
    int DiscordProxies,
    int DiscordSettings,
    int LinkTokens)
{
    public static LossyCounts None { get; } = new(0, 0, 0, 0, 0);

    public bool Any => GoogleIds + ExtraImages + DiscordProxies + DiscordSettings + LinkTokens > 0;
}

internal static class LossyScan
{
    public static LossyCounts Evaluate(StackSnapshot snapshot, StackKind target)
    {
        if (target == StackKind.Sqlite)
        {
            return new LossyCounts(
                GoogleIds: snapshot.Accounts.Count(account => !string.IsNullOrWhiteSpace(account.GoogleId)),
                ExtraImages: snapshot.Alters.Count(alter => alter.ExtraImages.Count > 0),
                DiscordProxies: snapshot.Alters.Count(alter => alter.DiscordProxies.Count > 0),
                DiscordSettings: snapshot.Accounts.Count(account => account.DiscordSettings is not null),
                LinkTokens: 0);
        }

        return new LossyCounts(
            GoogleIds: 0,
            ExtraImages: 0,
            DiscordProxies: 0,
            DiscordSettings: 0,
            LinkTokens: snapshot.Accounts.Count(account => !string.IsNullOrWhiteSpace(account.LinkToken)));
    }
}
