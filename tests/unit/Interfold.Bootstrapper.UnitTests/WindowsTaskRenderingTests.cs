using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class WindowsTaskRenderingTests
{
    private static WindowsScheduledTaskPhase.WindowsTaskRenderInput MakeInput(
        bool includeUpdate = false,
        bool includeSelfUpdate = false,
        string channel = "stable",
        bool autostart = true) => new(
        OutputDir: @"C:\srv\interfold\deploy",
        ComposeFile: @"C:\srv\interfold\deploy\docker-compose.yaml",
        ConfigPath: @"C:\srv\interfold\deploy\interfold.bootstrap.json",
        BinaryPath: @"C:\opt\interfold\interfold-bootstrap.exe",
        DockerPath: @"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
        CalendarTrigger: WindowsTaskSchedule.TryFromOnCalendar("daily", enabled: true, out var trigger, out _)
            ? trigger!
            : throw new InvalidOperationException("daily must translate"),
        AutostartEnabled: autostart,
        IncludeUpdateAction: includeUpdate,
        IncludeBootstrapperSelfUpdate: includeSelfUpdate,
        BootstrapperChannel: channel,
        UserId: @"DESKTOP-TEST\operator");

    [Test]
    public async Task InterfoldTaskUsesDockerComposeUp()
    {
        var rendered = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.InterfoldTemplate, MakeInput());

        await Assert.That(rendered).Contains(@"<Command>C:\Program Files\Docker\Docker\resources\bin\docker.exe</Command>");
        await Assert.That(rendered).Contains(@"compose -f ""C:\srv\interfold\deploy\docker-compose.yaml"" up -d");
        await Assert.That(rendered).Contains(@"<WorkingDirectory>C:\srv\interfold\deploy</WorkingDirectory>");
        await Assert.That(rendered).Contains("<LogonTrigger>");
        await Assert.That(rendered).Contains("<RunLevel>LeastPrivilege</RunLevel>");
        await Assert.That(rendered).Contains(@"<UserId>DESKTOP-TEST\operator</UserId>");
        await Assert.That(rendered).DoesNotContain("Highest");
        await Assert.That(rendered).DoesNotContain("<URI>");
    }

    [Test]
    public async Task AutostartFlagControlsLogonTriggerEnabled()
    {
        var on = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.InterfoldTemplate, MakeInput(autostart: true));
        var off = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.InterfoldTemplate, MakeInput(autostart: false));

        await Assert.That(on).Contains("<LogonTrigger>");
        await Assert.That(on).Contains("<Enabled>true</Enabled>");
        await Assert.That(off).Contains("<Enabled>false</Enabled>");
    }

    [Test]
    public async Task BackupTaskPointsAtBootstrapperBinary()
    {
        var rendered = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.BackupTemplate, MakeInput());

        await Assert.That(rendered).Contains(@"<Command>C:\opt\interfold\interfold-bootstrap.exe</Command>");
        await Assert.That(rendered).Contains(
            @"backup --config ""C:\srv\interfold\deploy\interfold.bootstrap.json"" --output-dir ""C:\srv\interfold\deploy"" --component all");
        await Assert.That(rendered).Contains("<CalendarTrigger>");
        await Assert.That(rendered).DoesNotContain("update-images");
        await Assert.That(rendered).DoesNotContain("<URI>");
    }

    [Test]
    public async Task UpdateActionIsSecondExecWhenEnabled()
    {
        var rendered = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.BackupTemplate, MakeInput(includeUpdate: true));

        await Assert.That(rendered).Contains("update-images --config");
        await Assert.That(rendered.IndexOf("backup --config", StringComparison.Ordinal))
            .IsLessThan(rendered.IndexOf("update-images --config", StringComparison.Ordinal));
        await Assert.That(rendered).DoesNotContain("update-self");
    }

    [Test]
    public async Task UpdateActionChainsSelfUpdateWhenEnabled()
    {
        var rendered = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.BackupTemplate,
            MakeInput(includeUpdate: true, includeSelfUpdate: true, channel: "bleeding-edge"));

        await Assert.That(rendered).Contains("update-self --non-interactive --channel bleeding-edge");
        await Assert.That(rendered).Contains("update-images --config");
        await Assert.That(rendered.IndexOf("update-self", StringComparison.Ordinal))
            .IsLessThan(rendered.IndexOf("update-images --config", StringComparison.Ordinal));
    }

    [Test]
    public async Task UpdateActionChainsPinnedChannelUnchanged()
    {
        var rendered = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.BackupTemplate,
            MakeInput(includeUpdate: true, includeSelfUpdate: true, channel: "v0.0.1"));

        await Assert.That(rendered).Contains("update-self --non-interactive --channel v0.0.1");
    }

    [Test]
    public async Task NoUnsubstitutedTokensRemain()
    {
        var input = MakeInput(includeUpdate: true);
        foreach (var template in WindowsScheduledTaskPhase.TemplateNames)
        {
            var rendered = WindowsScheduledTaskPhase.RenderTask(template, input);
            await Assert.That(rendered).DoesNotContain("{{");
            await Assert.That(rendered).DoesNotContain("}}");
        }
    }

    [Test]
    public async Task RenderTaskThrowsForUnknownTemplate()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => WindowsScheduledTaskPhase.RenderTask("does-not-exist.xml", MakeInput()));
        await Assert.That(ex.Message).Contains("does-not-exist.xml");
    }

    [Test]
    public async Task XmlEscapesAmpersandInPaths()
    {
        var input = MakeInput() with { OutputDir = @"C:\srv\a&b" };
        var rendered = WindowsScheduledTaskPhase.RenderTask(
            WindowsScheduledTaskPhase.InterfoldTemplate, input);
        await Assert.That(rendered).Contains(@"C:\srv\a&amp;b");
    }
}
