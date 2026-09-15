using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Translates the subset of systemd OnCalendar strings we accept on Windows into a
/// Task Scheduler trigger XML fragment. Untranslatable expressions fail install-service
/// rather than silently changing the operator's schedule.
/// </summary>
internal static class WindowsTaskSchedule
{
    private static readonly Regex DailyAtTime = new(
        @"^\*-\*-\*\s+(\d{1,2}):(\d{2})(?::(\d{2}))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryFromOnCalendar(
        string onCalendar,
        bool enabled,
        [NotNullWhen(true)] out string? triggerXml,
        [NotNullWhen(false)] out string? error)
    {
        triggerXml = null;
        error = null;
        var raw = (onCalendar ?? string.Empty).Trim();
        var enabledAttr = enabled ? "true" : "false";

        if (raw.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            triggerXml = DailyTrigger(TimeOnly.MinValue, enabledAttr);
            return true;
        }

        if (raw.Equals("weekly", StringComparison.OrdinalIgnoreCase))
        {
            triggerXml = WeeklyTrigger(enabledAttr);
            return true;
        }

        if (raw.Equals("hourly", StringComparison.OrdinalIgnoreCase))
        {
            triggerXml = HourlyTrigger(enabledAttr);
            return true;
        }

        var match = DailyAtTime.Match(raw);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            && hour is >= 0 and <= 23
            && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute)
            && minute is >= 0 and <= 59)
        {
            var second = 0;
            if (match.Groups[3].Success)
            {
                if (!int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out second)
                    || second is < 0 or > 59)
                {
                    error = Untranslatable(raw);
                    return false;
                }
            }

            triggerXml = DailyTrigger(new TimeOnly(hour, minute, second), enabledAttr);
            return true;
        }

        error = Untranslatable(raw);
        return false;
    }

    private static string Untranslatable(string onCalendar) =>
        $"config.deployment.backup.schedule='{onCalendar}' is a systemd OnCalendar expression that cannot be translated to Task Scheduler. Use 'daily', 'weekly', 'hourly', or '*-*-* HH:MM:SS'.";

    private static string DailyTrigger(TimeOnly time, string enabled) =>
        $"""
            <CalendarTrigger>
              <StartBoundary>2020-01-01T{time:HH\:mm\:ss}</StartBoundary>
              <Enabled>{enabled}</Enabled>
              <ScheduleByDay>
                <DaysInterval>1</DaysInterval>
              </ScheduleByDay>
            </CalendarTrigger>
        """;

    private static string WeeklyTrigger(string enabled) =>
        $"""
            <CalendarTrigger>
              <StartBoundary>2020-01-01T00:00:00</StartBoundary>
              <Enabled>{enabled}</Enabled>
              <ScheduleByWeek>
                <WeeksInterval>1</WeeksInterval>
                <DaysOfWeek>
                  <Monday />
                </DaysOfWeek>
              </ScheduleByWeek>
            </CalendarTrigger>
        """;

    private static string HourlyTrigger(string enabled) =>
        $"""
            <TimeTrigger>
              <Repetition>
                <Interval>PT1H</Interval>
                <StopAtDurationEnd>false</StopAtDurationEnd>
              </Repetition>
              <StartBoundary>2020-01-01T00:00:00</StartBoundary>
              <Enabled>{enabled}</Enabled>
            </TimeTrigger>
        """;
}
