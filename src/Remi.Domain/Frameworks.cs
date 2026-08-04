namespace Remi.Domain;

public enum FrameworkCode
{
    GCloud13,
    GCloud14,
    VerticalApplicationSolutions,
}

public sealed record FrameworkDefinition(
    FrameworkCode Code,
    string AgreementNumber,
    string DisplayName,
    string ReportingAuthority,
    string TemplateNotes);

public static class Frameworks
{
    public static readonly IReadOnlyList<FrameworkDefinition> All =
    [
        new(
            FrameworkCode.GCloud13,
            "RM1557.13",
            "G-Cloud 13",
            "GCA (formerly CCS)",
            "Historical template: service group and Digital Marketplace service ID are required for contracts and invoices."),
        new(
            FrameworkCode.GCloud14,
            "RM1557.14",
            "G-Cloud 14",
            "GCA (formerly CCS)",
            "Contracts and invoices use the G-Cloud 14 MI template. Preserve the official template format when exporting."),
        new(
            FrameworkCode.VerticalApplicationSolutions,
            "RM6259",
            "Vertical Application Solutions",
            "GCA (formerly CCS)",
            "The VAS template uses product/service and order-channel fields instead of G-Cloud service IDs."),
    ];

    public static FrameworkDefinition Get(FrameworkCode code) =>
        All.Single(framework => framework.Code == code);
}
