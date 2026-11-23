using System.Globalization;
using PdfKnead.Core.Policy;

namespace PdfKnead.Services;

/// <summary>
/// When the window that invites support may come (decided on 21.11.2026): 30 days after the first start at the earliest, then 9 months after
/// „Vielleicht später“ and 18 months after „Unterstützen“; only right after a successful save, never at the start, never in the middle of a
/// piece of work. There is no switch to turn it off in the program; an organization switches it off with its function file. Nothing of it
/// leaves the computer.
/// </summary>
public static class SupportPrompt
{
    /// <summary>The id of the window in the function file (<see cref="PolicyCatalog"/>).</summary>
    public const string PolicyId = "program.donation-window";

    /// <summary>Days after the first start before the window may come the first time.</summary>
    public const int FirstAfterDays = 30;

    /// <summary>Months until it may come again after „Vielleicht später“ (or closing it).</summary>
    public const int AgainAfterLaterMonths = 9;

    /// <summary>Months until it may come again after „Unterstützen“.</summary>
    public const int AgainAfterSupportMonths = 18;

    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>At the start: remembers the first day the program ran, once.</summary>
    public static void NoteStart(DateOnly today)
    {
        if (UserSettings.Current.FirstUseDay is null) UserSettings.Update(s => s with { FirstUseDay = Format(today) });
    }

    /// <summary>Whether the window may come now (the settings of this computer, the function file of the organization).</summary>
    public static bool Due(DateOnly today) =>
        ActivePolicy.Allows(PolicyId) && Due(today, UserSettings.Current.FirstUseDay, UserSettings.Current.SupportNextDay);

    /// <summary>The rule itself: due when the first start lies <see cref="FirstAfterDays"/> back and the earliest next day is reached.</summary>
    /// <param name="firstUseDay">The first day the program ran (yyyy-MM-dd); null or unreadable = not known yet, not due.</param>
    /// <param name="nextDay">The earliest day for the next time (yyyy-MM-dd); null = not set yet.</param>
    public static bool Due(DateOnly today, string? firstUseDay, string? nextDay)
    {
        if (Parse(firstUseDay) is not { } first || today < first.AddDays(FirstAfterDays)) return false;
        return Parse(nextDay) is not { } next || today >= next;
    }

    /// <summary>After the window: the earliest day for the next time.</summary>
    /// <param name="supported">True after „Unterstützen“, false after „Vielleicht später“ or closing.</param>
    public static void Answered(DateOnly today, bool supported) =>
        UserSettings.Update(s => s with { SupportNextDay = Format(NextDay(today, supported)) });

    /// <summary>The earliest day for the next time after an answer.</summary>
    public static DateOnly NextDay(DateOnly today, bool supported) =>
        today.AddMonths(supported ? AgainAfterSupportMonths : AgainAfterLaterMonths);

    private static string Format(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static DateOnly? Parse(string? day) =>
        DateOnly.TryParseExact(day, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
}
