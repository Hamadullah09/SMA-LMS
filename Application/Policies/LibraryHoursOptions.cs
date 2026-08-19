using System.Globalization;

namespace Library_Management_system.Application.Policies;

/// <summary>
/// When the library is open.
/// </summary>
/// <remarks>
/// The same opening hours were written out by hand in three places — the home page, the About page
/// and the assistant's canned reply — and had already drifted: the assistant told students
/// "Monday-Friday 8:00 AM-8:00 PM, Saturday-Sunday 9:00 AM-5:00 PM" while both pages printed
/// 07:30-20:30 and 07:30-16:30. One source now feeds all three.
///
/// Bound from the "LibraryHours" configuration section when present. The defaults are the hours the
/// site already published, so an install with no configuration keeps the behaviour it had.
/// </remarks>
public sealed class LibraryHoursOptions
{
    public const string SectionName = "LibraryHours";

    public TimeOnly WeekdayOpen { get; set; } = new(7, 30);
    public TimeOnly WeekdayClose { get; set; } = new(20, 30);
    public TimeOnly WeekendOpen { get; set; } = new(7, 30);
    public TimeOnly WeekendClose { get; set; } = new(16, 30);

    /// <summary>
    /// The library's own timezone, used to decide whether it is open right now.
    /// </summary>
    /// <remarks>
    /// Not the server's. The hosted deployment runs on a machine set to UTC, so comparing the
    /// opening hours against DateTime.Now there reported the library closed at 05:50 UTC while it
    /// was 10:50 and open in Karachi. Opening hours are wall-clock times in the library's town, so
    /// the comparison has to be made in the library's zone wherever the code happens to run.
    ///
    /// Accepts either an IANA name ("Asia/Karachi") or a Windows one ("Pakistan Standard Time");
    /// which of the two a machine recognises depends on its ICU data, so both are tried.
    /// </remarks>
    public string TimeZone { get; set; } = "Asia/Karachi";

    /// <summary>Now, in the library's timezone.</summary>
    public DateTime LocalNow()
    {
        var zone = ResolveZone();

        return zone is null
            ? DateTime.Now                                   // no such zone on this machine
            : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }

    private TimeZoneInfo? ResolveZone()
    {
        if (string.IsNullOrWhiteSpace(TimeZone))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows and Linux disagree on the identifier. Translate and try the other spelling.
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(TimeZone, out var windowsId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
                catch (TimeZoneNotFoundException) { }
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZone, out var ianaId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
                catch (TimeZoneNotFoundException) { }
            }

            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Shown alongside the hours, e.g. to note holiday closures.</summary>
    public string? Note { get; set; } = "Except public holidays.";

    private static string Format(TimeOnly time) =>
        time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string WeekdayRange => $"{Format(WeekdayOpen)} – {Format(WeekdayClose)}";

    public string WeekendRange => $"{Format(WeekendOpen)} – {Format(WeekendClose)}";

    /// <summary>One-line phrasing, for the assistant.</summary>
    public string Sentence =>
        $"Monday to Friday we are open {WeekdayRange}, and Saturday and Sunday {WeekendRange}."
        + (string.IsNullOrWhiteSpace(Note) ? string.Empty : $" {Note}");

    /// <summary>Whether the library is open at the given local time.</summary>
    public bool IsOpenAt(DateTime localTime)
    {
        var isWeekend = localTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var open = isWeekend ? WeekendOpen : WeekdayOpen;
        var close = isWeekend ? WeekendClose : WeekdayClose;
        var now = TimeOnly.FromDateTime(localTime);

        return now >= open && now < close;
    }
}
