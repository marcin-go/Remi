using System.Globalization;
using Remi.Application;

namespace Remi.Web;

public static class HomeRoutes
{
    public static int Horizon(int? days) => days is 30 or 90 or 180 ? days.Value : 180;
    public static string Month(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static DateOnly ParseMonth(string? value, DateOnly fallback) =>
        DateOnly.TryParseExact($"{value}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : new DateOnly(fallback.Year, fallback.Month, 1);
    public static string Contracts(string status, int horizon = 180) => $"/contracts?status={Uri.EscapeDataString(status)}&horizon={Horizon(horizon)}";
    public static string Planned(DateOnly month, string? review = null) => $"/invoices?view=planned&month={Month(month)}"
        + (review is "undated" or "overdue" ? $"&review={review}" : "");
    public static string Contract(Guid id, string section, string? returnTo = null) => $"/contracts/{id}?section={section}"
        + (SafeReturn(returnTo) is string back ? $"&returnTo={Uri.EscapeDataString(back)}" : "")
        + (section == "schedule" ? "#contract-payment-schedule" : "");

    public static string? SafeReturn(string? path) => path is not null && !path.Contains('\\') && !path.Any(char.IsControl)
        && new[] { "/home", "/contracts", "/invoices", "/reports" }.Any(root => path == root || path.StartsWith(root + "?", StringComparison.Ordinal))
            ? path : null;

    public static PortfolioFilter Filter(string? status) => status switch
    {
        "live" => PortfolioFilter.Live, "future" => PortfolioFilter.Future, "unknown" => PortfolioFilter.UnknownDates,
        "ended" => PortfolioFilter.Ended, "ending-soon" => PortfolioFilter.Ending,
        "ending-no-option" => PortfolioFilter.EndingNoOptionRecorded, "ending-options" => PortfolioFilter.EndingOptionsRecorded,
        "ending-review" => PortfolioFilter.EndingOptionsNeedReview, "no-schedule" => PortfolioFilter.NoSchedule,
        "extension-review" => PortfolioFilter.ExtensionReview, "ended-value" => PortfolioFilter.EndedValue,
        "undated" => PortfolioFilter.UndatedSchedule, _ => PortfolioFilter.All,
    };
    public static string OptionLabel(OperationalContract item) => item.OptionState switch
    {
        RecordedOptionState.OptionsRecorded => $"Option year{(item.RecordedOptionYears.Count == 1 ? "" : "s")} {string.Join(", ", item.RecordedOptionYears)} recorded",
        RecordedOptionState.AssociationNeedsReview => "Extension association needs review",
        _ => "No further extension recorded",
    };
}
