using System.Globalization;

namespace Remi.Domain;

/// <summary>
/// Describes the contractual reporting deadline for a framework. The policy is attached to the
/// framework definition so deadlines are never applied as a global reporting rule.
/// </summary>
public enum ReportingDeadlineRule
{
    CalendarDayOfFollowingMonth,
    WorkingDayOfFollowingMonth,
}

public sealed record ReportingDeadlinePolicy(ReportingDeadlineRule Rule, int Occurrence)
{
    public DateOnly? Calculate(string reportingMonth)
    {
        if (!DateOnly.TryParseExact(
                $"{reportingMonth}-01",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var monthStart) || Occurrence <= 0)
        {
            return null;
        }

        var followingMonth = monthStart.AddMonths(1);
        return Rule switch
        {
            ReportingDeadlineRule.CalendarDayOfFollowingMonth when Occurrence <= DateTime.DaysInMonth(followingMonth.Year, followingMonth.Month) =>
                new DateOnly(followingMonth.Year, followingMonth.Month, Occurrence),
            ReportingDeadlineRule.WorkingDayOfFollowingMonth => NthWeekday(followingMonth, Occurrence),
            _ => null,
        };
    }

    public string Description => Rule switch
    {
        ReportingDeadlineRule.CalendarDayOfFollowingMonth => $"{Ordinal(Occurrence)} calendar day of the following month",
        ReportingDeadlineRule.WorkingDayOfFollowingMonth => $"{Ordinal(Occurrence)} working day of the following month",
        _ => "configured reporting deadline",
    };

    private static DateOnly NthWeekday(DateOnly monthStart, int occurrence)
    {
        var current = monthStart;
        var found = 0;
        while (true)
        {
            if (current.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && ++found == occurrence)
            {
                return current;
            }

            current = current.AddDays(1);
        }
    }

    private static string Ordinal(int value) => (value % 100) is 11 or 12 or 13
        ? $"{value}th"
        : (value % 10) switch
        {
            1 => $"{value}st",
            2 => $"{value}nd",
            3 => $"{value}rd",
            _ => $"{value}th",
        };
}
