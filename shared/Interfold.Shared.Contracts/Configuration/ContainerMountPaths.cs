namespace Interfold.Shared.Contracts.Configuration;

/// <summary>In-container mount points paired with <see cref="ComposeVolumes"/>. Changing
/// any value is a compose-wire change: existing named volumes would silently orphan.</summary>
public static class ContainerMountPaths
{
    /// <summary>Pre-created in-image with app:app ownership so the volume inherits the owner on first mount.</summary>
    public const string InterfoldAvatars = "/app/data/avatars";

    /// <summary>Parent dir for the SQLite database file when <c>OCTOCON_PERSISTENCE=sqlite</c>.</summary>
    public const string InterfoldSqliteData = "/app/data";

    /// <summary>Canonical SQLite database filename under <see cref="InterfoldSqliteData"/>.</summary>
    public const string InterfoldSqliteDbFileName = "interfold.db";
}
