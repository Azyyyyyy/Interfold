using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Windows counterpart of <see cref="SystemdInstallPhase"/>: writes Task
/// Scheduler XML and registers current-user tasks (LeastPrivilege, not Highest).</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScheduledTaskPhase
{
    private static readonly string Phase = BootstrapCommand.InstallService.ToPhaseLogName();

    internal const string InterfoldTemplate = "interfold.xml";
    internal const string BackupTemplate = "interfold-backup.xml";

    internal static readonly string[] TemplateNames = [InterfoldTemplate, BackupTemplate];

    internal sealed record WindowsTaskRenderInput(
        string OutputDir,
        string ComposeFile,
        string ConfigPath,
        string BinaryPath,
        string DockerPath,
        string CalendarTrigger,
        bool AutostartEnabled,
        bool IncludeUpdateAction,
        bool IncludeBootstrapperSelfUpdate,
        string BootstrapperChannel,
        string UserId);

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var config = await PhaseArtifactLoader
            .LoadRequiredConfigAsync(options, logger, Phase, "install-service", ct)
            .ConfigureAwait(false);
        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);

        var enableAutostart = options.EnableAutostart || config.Deployment.AutostartServer;
        var enableBackupTimer = options.EnableBackupTimer || config.Deployment.Backup.Enabled;

        if (!WindowsTaskSchedule.TryFromOnCalendar(
                config.Deployment.Backup.Schedule, enableBackupTimer,
                out var calendarTrigger, out var calendarError))
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.InvalidCalendar);
            throw new InvalidOperationException(calendarError);
        }

        var xmlDir = options.SystemdUnitDir
                     ?? Path.Combine(options.OutputDir, "scheduled-tasks");
        var binaryPath = HostPaths.ResolveBootstrapperBinary(options.BinaryPathOverride);
        var composeFile = BootstrapArtifactPaths.ResolveComposeFileOrConventional(options.OutputDir);
        var dockerPath = PathLookup.TryFind(
                             "docker",
                             Environment.GetEnvironmentVariable("PATH"),
                             Environment.GetEnvironmentVariable("PATHEXT"),
                             windows: true)
                         ?? "docker";

        var renderInput = new WindowsTaskRenderInput(
            OutputDir: options.OutputDir,
            ComposeFile: composeFile,
            ConfigPath: Path.GetFullPath(configPath),
            BinaryPath: binaryPath,
            DockerPath: dockerPath,
            CalendarTrigger: calendarTrigger,
            AutostartEnabled: enableAutostart,
            IncludeUpdateAction: config.Deployment.Update.Enabled,
            IncludeBootstrapperSelfUpdate: config.Deployment.Update.Bootstrapper.Enabled,
            BootstrapperChannel: config.Deployment.Update.Bootstrapper.Channel.ToWireValue(),
            // InteractiveToken requires an explicit principal; omit → Access Denied on /Create.
            UserId: WindowsIdentity.GetCurrent().Name);

        logger.Info($"    rendering Task Scheduler XML to {xmlDir}");
        Directory.CreateDirectory(xmlDir);

        var interfoldXml = Path.Combine(xmlDir, InterfoldTemplate);
        var backupXml = Path.Combine(xmlDir, BackupTemplate);
        await WriteUtf16Async(interfoldXml, RenderTask(InterfoldTemplate, renderInput), ct).ConfigureAwait(false);
        logger.Info($"    wrote {interfoldXml}");
        await WriteUtf16Async(backupXml, RenderTask(BackupTemplate, renderInput), ct).ConfigureAwait(false);
        logger.Info($"    wrote {backupXml}");

        if (options.SystemdUnitDir is not null)
        {
            logger.Info("    --systemd-unit-dir set; skipping schtasks (test mode)");
            logger.PhaseDone(Phase);
            return 0;
        }

        await SchtasksCreateAsync(WindowsTaskNames.Interfold, interfoldXml, logger, ct).ConfigureAwait(false);
        await SchtasksCreateAsync(WindowsTaskNames.Backup, backupXml, logger, ct).ConfigureAwait(false);

        if (enableAutostart)
        {
            logger.Info("    bringing compose stack up (logon task is registered; not using schtasks /Run)");
            var up = await DockerCompose.UpAsync(composeFile, ct: ct).ConfigureAwait(false);
            if (up.ExitCode != 0)
            {
                logger.Warn($"docker compose up -d exited {up.ExitCode}: {up.StdErr.Trim()}");
            }
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>Reads the embedded template, substitutes every <c>{{TOKEN}}</c>, and returns
    /// the rendered XML. Internal so tests can drive the renderer without touching disk.</summary>
    internal static string RenderTask(string templateName, WindowsTaskRenderInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        ArgumentNullException.ThrowIfNull(input);

        var resourceName = $"windows-task/{templateName}";
        var asm = typeof(WindowsScheduledTaskPhase).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Task Scheduler template '{resourceName}' missing. " +
                "Check Interfold.Bootstrapper.csproj's <EmbeddedResource> entries.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var template = reader.ReadToEnd();

        var updateAction = BuildUpdateActionXml(input);

        var sb = new StringBuilder(template);
        sb.Replace("{{UPDATE_ACTION}}", updateAction);
        sb.Replace("{{CALENDAR_TRIGGER}}", input.CalendarTrigger);
        sb.Replace("{{AUTOSTART_ENABLED}}", input.AutostartEnabled ? "true" : "false");
        sb.Replace("{{OUTPUT_DIR}}", XmlEscape(input.OutputDir));
        sb.Replace("{{COMPOSE_FILE}}", XmlEscape(input.ComposeFile));
        sb.Replace("{{CONFIG_PATH}}", XmlEscape(input.ConfigPath));
        sb.Replace("{{BINARY_PATH}}", XmlEscape(input.BinaryPath));
        sb.Replace("{{DOCKER_PATH}}", XmlEscape(input.DockerPath));
        sb.Replace("{{USER_ID}}", XmlEscape(input.UserId));
        var rendered = sb.ToString();

        if (rendered.Contains("{{", StringComparison.Ordinal))
        {
            var openIdx = rendered.IndexOf("{{", StringComparison.Ordinal);
            var closeIdx = rendered.IndexOf("}}", openIdx, StringComparison.Ordinal);
            var snippet = closeIdx > openIdx
                ? rendered.Substring(openIdx, closeIdx - openIdx + 2)
                : rendered[openIdx..Math.Min(rendered.Length, openIdx + 40)];
            throw new InvalidOperationException(
                $"Task Scheduler template '{templateName}' contains an unsubstituted token '{snippet}'. " +
                "Add the matching replacement to WindowsScheduledTaskPhase.RenderTask.");
        }

        return rendered;
    }

    /// <summary>Mirrors <see cref="SystemdInstallPhase.BuildUpdateExecStart"/>: optional
    /// <c>update-self</c> Exec before <c>update-images</c> (Task Scheduler runs Actions in order).</summary>
    internal static string BuildUpdateActionXml(WindowsTaskRenderInput input)
    {
        if (!input.IncludeUpdateAction)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (input.IncludeBootstrapperSelfUpdate)
        {
            var channel = string.IsNullOrWhiteSpace(input.BootstrapperChannel)
                ? BootstrapperReleaseChannel.Stable.ToWireValue()
                : input.BootstrapperChannel;
            sb.Append(
                $"""
                      <Exec>
                        <Command>{{{{BINARY_PATH}}}}</Command>
                        <Arguments>update-self --non-interactive --channel {XmlEscape(channel)}</Arguments>
                        <WorkingDirectory>{{{{OUTPUT_DIR}}}}</WorkingDirectory>
                      </Exec>

                """);
        }

        sb.Append(
            """
                  <Exec>
                    <Command>{{BINARY_PATH}}</Command>
                    <Arguments>update-images --config "{{CONFIG_PATH}}" --output-dir "{{OUTPUT_DIR}}"</Arguments>
                    <WorkingDirectory>{{OUTPUT_DIR}}</WorkingDirectory>
                  </Exec>
            """);
        return sb.ToString();
    }

    private static string XmlEscape(string value) => SecurityElement.Escape(value)!;

    private static async Task WriteUtf16Async(string path, string xml, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, xml, Encoding.Unicode, ct).ConfigureAwait(false);
    }

    private static async Task SchtasksCreateAsync(
        string taskName, string xmlPath, PhaseLogger logger, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync(
            "schtasks",
            ["/Create", "/TN", taskName, "/XML", xmlPath, "/F"],
            ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.Schtasks);
            throw new InvalidOperationException(
                $"schtasks /Create /TN {taskName} exited {run.ExitCode}: {run.StdErr.Trim()}");
        }
        logger.Info($"    registered scheduled task {taskName}");
    }
}
