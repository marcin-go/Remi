namespace Remi.Web;

/// <summary>
/// Holds the reporting period selected for the current interactive Remi session.
/// The layout synchronises this state with the optional <c>period</c> URL query parameter.
/// </summary>
public sealed class ReportingPeriodContext
{
    private readonly TimeProvider timeProvider;

    public ReportingPeriodContext(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        SelectedPeriod = DefaultPeriod();
    }

    public event Action? Changed;

    public IReadOnlyList<string> AvailablePeriods { get; private set; } = [];

    public string SelectedPeriod { get; private set; }

    public bool IsInitialised { get; private set; }

    public void Synchronise(IEnumerable<string> availablePeriods, string? requestedPeriod)
    {
        var defaultPeriod = DefaultPeriod();
        var periods = availablePeriods
            .Where(IsValidPeriod)
            .Append(defaultPeriod)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(period => period, StringComparer.Ordinal)
            .ToList();

        // A reporting cycle is reviewed in the following calendar month. Keep that
        // calculated period available before any contracts, invoices or return exist for it.
        var selectedPeriod = requestedPeriod is null && IsInitialised && periods.Contains(SelectedPeriod, StringComparer.Ordinal)
            ? SelectedPeriod
            : IsValidPeriod(requestedPeriod) && periods.Contains(requestedPeriod, StringComparer.Ordinal)
                ? requestedPeriod
                : defaultPeriod;
        var changed = !string.Equals(SelectedPeriod, selectedPeriod, StringComparison.Ordinal)
            || !AvailablePeriods.SequenceEqual(periods, StringComparer.Ordinal)
            || !IsInitialised;

        AvailablePeriods = periods;
        SelectedPeriod = selectedPeriod!;
        IsInitialised = true;

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public static bool IsValidPeriod(string? period) =>
        !string.IsNullOrWhiteSpace(period)
        && DateOnly.TryParseExact($"{period}-01", "yyyy-MM-dd", out _);

    private string DefaultPeriod() => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime).AddMonths(-1).ToString("yyyy-MM");
}
