namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Task Scheduler names the Windows <c>install-service</c> path registers. Frozen
/// operator contract — keep in sync with the XML templates under
/// <c>Phases/WindowsTaskTemplates/</c>.
/// </summary>
internal static class WindowsTaskNames
{
    public const string Interfold = "Interfold";
    public const string Backup = "InterfoldBackup";
}
