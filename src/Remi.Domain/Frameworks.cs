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
    bool AllowsNewContracts,
    DateOnly? DefaultStartDate);

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
            false,
            new DateOnly(2022, 11, 9)),
        new(
            FrameworkCode.GCloud14,
            "RM1557.14",
            "G-Cloud 14",
            "GCA (formerly CCS)",
            "Contracts and invoices use the G-Cloud 14 MI template. Preserve the official template format when exporting.",
            true,
            new DateOnly(2024, 10, 29)),
        new(
            FrameworkCode.VerticalApplicationSolutions,
            "RM6259",
            "Vertical Application Solutions",
            "GCA (formerly CCS)",
            "The VAS template uses product/service and order-channel fields instead of G-Cloud service IDs.",
            true,
            new DateOnly(2023, 3, 7)),
        new(
            FrameworkCode.GCloud15,
            "Catalogue pending publication",
            "G-Cloud 15",
            "GCA (formerly CCS)",
            "StatMap's enrolment is known, but its public Digital Marketplace service catalogue and IDs have not yet been published.",
            true,
            null),
    ];

    public static FrameworkDefinition Get(FrameworkCode code) =>
        All.Single(framework => framework.Code == code);

    public static bool IsGCloud(FrameworkCode code) =>
        code is FrameworkCode.GCloud13 or FrameworkCode.GCloud14 or FrameworkCode.GCloud15;

    public static bool AllowsNewContracts(FrameworkCode code) =>
        Get(code).AllowsNewContracts;
}
