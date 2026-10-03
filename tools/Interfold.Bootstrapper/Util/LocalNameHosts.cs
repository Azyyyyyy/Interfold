using System.ComponentModel;
using System.Diagnostics;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Spectre.Console;

namespace Interfold.Bootstrapper.Util;

// mDNS publishes only the computer's own name. api.host.local is a different name,
// so it stays unresolved until the hosts file points it at loopback, where nginx listens.
internal static class LocalNameHosts
{
    internal const string BeginMark = "# interfold-local-names";
    internal const string EndMark = "# end interfold-local-names";
    internal const string ElevationQuestion =
        "Allow an administrator prompt to update the hosts file?";

    internal static IReadOnlyList<string> Aliases(BootstrapConfig config)
    {
        if (config.Edge.Cloudflare.Enabled || config.Edge.Routing.Mode != EdgeRoutingMode.Subdomain)
            return [];

        var names = new List<string>(2);
        Add(names, config.Edge.Routing.ApiHost);
        Add(names, config.Edge.Routing.WebHost);
        return names;
    }

    internal static string Merge(string existing, IReadOnlyList<string> names)
    {
        var stripped = RemoveMarked(existing);
        if (names.Count == 0)
            return stripped;

        var block =
            BeginMark + "\n" +
            "127.0.0.1 " + string.Join(' ', names) + "\n" +
            "::1 " + string.Join(' ', names) + "\n" +
            EndMark + "\n";

        if (stripped.Length == 0)
            return block;

        var separator = stripped.EndsWith('\n') ? string.Empty : "\n";
        return stripped + separator + block;
    }

    internal static void Apply(
        BootstrapConfig config,
        PhaseLogger logger,
        string? hostsPath = null,
        bool canPrompt = false,
        Func<bool>? confirmElevation = null,
        Action<string, string>? write = null,
        Func<string, string, bool>? writeElevated = null)
    {
        var names = Aliases(config);
        var path = hostsPath ?? DefaultPath();
        string existing;
        try
        {
            existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex)
        {
            logger.Warn($"    could not read {path}: {ex.Message}");
            WarnManual(logger, names);
            return;
        }

        var merged = Merge(existing, names);
        if (merged == existing)
            return;

        var writer = write ?? File.WriteAllText;
        try
        {
            writer(path, merged);
            LogApplied(logger, names);
            if (hostsPath is null && names.Count > 0)
                TryFlushDns(logger);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            // UAC or sudo covers only this write. The bootstrapper process stays as the operator.
            if (TryElevatedUpdate(path, merged, canPrompt, confirmElevation, writeElevated, logger))
            {
                LogApplied(logger, names);
                if (hostsPath is null && names.Count > 0)
                    TryFlushDns(logger);
                return;
            }

            logger.Warn($"    could not update {path}: {ex.Message}");
            WarnManual(logger, names);
        }
        catch (Exception ex)
        {
            logger.Warn($"    could not update {path}: {ex.Message}");
            WarnManual(logger, names);
        }
    }

    internal static string DefaultPath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
            : "/etc/hosts";

    private static void Add(List<string> names, string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Length == 0 || !trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return;
        if (names.Exists(n => n.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return;
        names.Add(trimmed);
    }

    private static string RemoveMarked(string text)
    {
        var start = text.IndexOf(BeginMark, StringComparison.Ordinal);
        if (start < 0)
            return text;

        var end = text.IndexOf(EndMark, start, StringComparison.Ordinal);
        if (end < 0)
            return text;

        end += EndMark.Length;
        if (end < text.Length && text[end] == '\r')
            end++;
        if (end < text.Length && text[end] == '\n')
            end++;

        return text.Remove(start, end - start);
    }

    private static void LogApplied(PhaseLogger logger, IReadOnlyList<string> names) =>
        logger.Info(names.Count == 0
            ? "    removed .local aliases from the hosts file"
            : $"    hosts file maps {string.Join(", ", names)} to this computer");

    private static bool TryElevatedUpdate(
        string path,
        string merged,
        bool canPrompt,
        Func<bool>? confirmElevation,
        Func<string, string, bool>? writeElevated,
        PhaseLogger logger)
    {
        if (!canPrompt)
            return false;

        var accepted = confirmElevation?.Invoke()
            ?? (!Console.IsInputRedirected && AnsiConsole.Confirm(ElevationQuestion, defaultValue: true));
        if (!accepted)
            return false;

        if (writeElevated is not null)
            return writeElevated(path, merged);

        return CopyWithElevation(path, merged, logger);
    }

    private static bool CopyWithElevation(string destination, string content, PhaseLogger logger)
    {
        var source = Path.Combine(Path.GetTempPath(), "interfold-hosts-" + Guid.NewGuid().ToString("n"));
        try
        {
            File.WriteAllText(source, content);
            if (OperatingSystem.IsWindows())
                return RunWindowsElevatedCopy(source, destination, logger);
            return RunSudoCopy(source, destination, logger);
        }
        catch (Exception ex)
        {
            logger.Warn($"    administrator update failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(source); }
            catch { /* best-effort cleanup, tolerate a locked temp file */ }
        }
    }

    private static bool RunWindowsElevatedCopy(string source, string destination, PhaseLogger logger)
    {
        var command =
            "$ErrorActionPreference='Stop'; " +
            $"Copy-Item -LiteralPath {PsLiteral(source)} -Destination {PsLiteral(destination)} -Force; " +
            "ipconfig /flushdns | Out-Null";
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                logger.Warn($"    administrator update exited {proc.ExitCode}");
                return false;
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    private static bool RunSudoCopy(string source, string destination, PhaseLogger logger)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("cp");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(destination);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                logger.Warn($"    sudo exited {proc.ExitCode}");
                return false;
            }

            return true;
        }
        catch (Win32Exception ex)
        {
            logger.Warn($"    sudo failed: {ex.Message}");
            return false;
        }
    }

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && ex.HResult is 5 or 13 or unchecked((int)0x80070005));

    private static void TryFlushDns(PhaseLogger logger)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A failed lookup stays cached, so the new hosts rows would still miss.
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            proc?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            logger.Warn($"    could not flush the DNS cache: {ex.Message}");
        }
    }

    private static void WarnManual(PhaseLogger logger, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return;

        logger.Warn(
            "    add these lines to the hosts file so this computer can open the two web addresses:\n" +
            "    127.0.0.1 " + string.Join(' ', names) + "\n" +
            "    ::1 " + string.Join(' ', names));
    }
}
