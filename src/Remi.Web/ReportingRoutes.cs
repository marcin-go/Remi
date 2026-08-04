namespace Remi.Web;

/// <summary>
/// Builds internal links without losing the reporting-period context.
/// </summary>
public static class ReportingRoutes
{
    public static string WithPeriod(string path, string reportingPeriod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportingPeriod);

        var separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{path}{separator}period={Uri.EscapeDataString(reportingPeriod)}";
    }
}
