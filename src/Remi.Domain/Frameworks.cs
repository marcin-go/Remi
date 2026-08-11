namespace Remi.Domain;

public enum FrameworkCode
{
    GCloud13,
    GCloud14,
    VerticalApplicationSolutions,
    // Appended to preserve the stored numeric values of the existing SQLite framework codes.
    GCloud15,
}

public sealed record FrameworkDefinition(
    FrameworkCode Code,
    string AgreementNumber,
    string DisplayName,
    string ReportingAuthority,
    string TemplateNotes,
    DateOnly? DefaultStartDate,
    DateOnly? DefaultEndDate,
    ReportingDeadlinePolicy? ReportingDeadline)
{
    public bool IsHistorical => DefaultEndDate is { } endDate
        && endDate < DateOnly.FromDateTime(DateTime.Today);
}

public static class Frameworks
{
    public static readonly IReadOnlyList<FrameworkDefinition> All =
    [
        new(
            FrameworkCode.GCloud13,
            "RM1557.13",
            "G-Cloud 13",
            "GCA (formerly CCS)",
            "Historical template: service group and Digital Marketplace service ID are required for contracts and invoices.",
            new DateOnly(2022, 11, 9),
            new DateOnly(2024, 11, 8),
            new ReportingDeadlinePolicy(ReportingDeadlineRule.CalendarDayOfFollowingMonth, 7)),
        new(
            FrameworkCode.GCloud14,
            "RM1557.14",
            "G-Cloud 14",
            "GCA (formerly CCS)",
            "Contracts and invoices use the G-Cloud 14 MI template. Preserve the official template format when exporting.",
            new DateOnly(2024, 10, 29),
            new DateOnly(2026, 10, 28),
            new ReportingDeadlinePolicy(ReportingDeadlineRule.WorkingDayOfFollowingMonth, 5)),
        new(
            FrameworkCode.VerticalApplicationSolutions,
            "RM6259",
            "Vertical Application Solutions",
            "GCA (formerly CCS)",
            "The VAS template uses product/service and order-channel fields instead of G-Cloud service IDs.",
            new DateOnly(2023, 3, 7),
            new DateOnly(2027, 3, 6),
            new ReportingDeadlinePolicy(ReportingDeadlineRule.CalendarDayOfFollowingMonth, 7)),
        new(
            FrameworkCode.GCloud15,
            "Catalogue pending publication",
            "G-Cloud 15",
            "GCA (formerly CCS)",
            "StatMap's enrolment is known, but its approved MI template and public Digital Marketplace service catalogue have not yet been published.",
            null,
            null,
            null),
    ];

    public static FrameworkDefinition Get(FrameworkCode code) =>
        All.Single(framework => framework.Code == code);

    public static bool IsGCloud(FrameworkCode code) =>
        code is FrameworkCode.GCloud13 or FrameworkCode.GCloud14 or FrameworkCode.GCloud15;

    public static bool AllowsNewContracts(FrameworkCode code) =>
        IsOperational(code, DateOnly.FromDateTime(DateTime.Today));

    public static bool IsOperational(FrameworkCode code, DateOnly date)
    {
        var framework = Get(code);
        return framework.DefaultStartDate is { } startDate
            && framework.DefaultEndDate is { } endDate
            && date >= startDate
            && date <= endDate;
    }
}
