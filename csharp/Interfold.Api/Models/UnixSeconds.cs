using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Interfold.Api.Models;

/// <summary>
/// A validated Unix-seconds query anchor (fronting history endpoints). Encapsulates both
/// the numeric parse and <see cref="DateTimeOffset.FromUnixTimeSeconds"/>'s range check so
/// callers get a single TryParse and can keep emitting their legacy
/// <c>invalid_anchor</c>/<c>invalid_end_anchor</c> error bodies (binding stays string —
/// a typed model bind would surface MVC's generic 400 instead).
/// </summary>
public readonly record struct UnixSeconds(long Value)
{
    public DateTimeOffset ToDateTimeOffset() => DateTimeOffset.FromUnixTimeSeconds(Value);

    public static bool TryParse([NotNullWhen(true)] string? raw, out UnixSeconds result)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds()
            && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            result = new UnixSeconds(seconds);
            return true;
        }

        result = default;
        return false;
    }
}
