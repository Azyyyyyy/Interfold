namespace Interfold.Settings.Contracts.Models.Wire;

/// <summary>Whether a Discord bot is currently connected to this API instance.</summary>
public sealed record DiscordBotStatusResponse(bool Active);
