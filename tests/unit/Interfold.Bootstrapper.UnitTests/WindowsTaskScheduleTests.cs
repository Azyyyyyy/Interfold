using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class WindowsTaskScheduleTests
{
    [Test]
    public async Task DailyMapsToCalendarTriggerAtMidnight()
    {
        var ok = WindowsTaskSchedule.TryFromOnCalendar("daily", enabled: true, out var xml, out var error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(xml).Contains("<CalendarTrigger>");
        await Assert.That(xml).Contains("2020-01-01T00:00:00");
        await Assert.That(xml).Contains("<DaysInterval>1</DaysInterval>");
        await Assert.That(xml).Contains("<Enabled>true</Enabled>");
    }

    [Test]
    public async Task DailyIsCaseInsensitive()
    {
        var ok = WindowsTaskSchedule.TryFromOnCalendar("Daily", enabled: false, out var xml, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(xml).Contains("<Enabled>false</Enabled>");
    }

    [Test]
    public async Task WeeklyMapsToMonday()
    {
        var ok = WindowsTaskSchedule.TryFromOnCalendar("weekly", enabled: true, out var xml, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(xml).Contains("<ScheduleByWeek>");
        await Assert.That(xml).Contains("<Monday />");
    }

    [Test]
    public async Task HourlyMapsToTimeTrigger()
    {
        var ok = WindowsTaskSchedule.TryFromOnCalendar("hourly", enabled: true, out var xml, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(xml).Contains("<TimeTrigger>");
        await Assert.That(xml).Contains("<Interval>PT1H</Interval>");
    }

    [Test]
    public async Task StarDateWithTimeMapsToDailyCalendar()
    {
        var ok = WindowsTaskSchedule.TryFromOnCalendar("*-*-* 03:30:00", enabled: true, out var xml, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(xml).Contains("2020-01-01T03:30:00");
        await Assert.That(xml).Contains("<DaysInterval>1</DaysInterval>");
    }

    [Test]
    public async Task StarDateWithHhMmIsAccepted()
    {
        var ok = WindowsTaskSchedule.TryFromOnCalendar("*-*-* 7:05", enabled: true, out var xml, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(xml).Contains("2020-01-01T07:05:00");
    }

    [Test]
    public async Task UntranslatableOnCalendarFailsWithMessage()
    {
        var ok = WindowsTaskSchedule.TryFromOnCalendar("Mon..Fri 03:30", enabled: true, out var xml, out var error);
        await Assert.That(ok).IsFalse();
        await Assert.That(xml).IsNull();
        await Assert.That(error).Contains("Mon..Fri 03:30");
        await Assert.That(error).Contains("cannot be translated to Task Scheduler");
    }
}
