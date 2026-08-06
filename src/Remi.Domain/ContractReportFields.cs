namespace Remi.Domain;

/// <summary>
/// Exact contract-column labels from each supported GCA MI workbook.
/// A framework is available here only when Remi has a supplied report template to follow.
/// </summary>
public sealed record ContractReportFieldNames(
    string SupplierReferenceNumber,
    string CustomerUniqueReferenceNumber,
    string CustomerOrganisationName,
    string ContractStartDate,
    string ContractEndDate,
    string LotNumber,
    string? ServiceGroup,
    string? DigitalMarketplaceServiceId,
    string? ProductServiceDescription,
    string? OrderChannel,
    string TotalContractValue);

public static class ContractReportFields
{
    public static readonly IReadOnlyList<string> VasOrderChannels =
    [
        "Direct Award",
        "Further Competition",
    ];

    private static readonly ContractReportFieldNames GCloud13 = new(
        "Supplier Reference Number",
        "Customer Unique Reference Number (URN)",
        "Customer Organisation Name",
        "Contract Start Date",
        "Contract End Date",
        "Lot Number",
        "Service Group",
        "Digital Marketplace Service ID",
        null,
        null,
        "Total Contract Value");

    private static readonly ContractReportFieldNames GCloud14 = new(
        "Supplier reference number",
        "Customer Unique Reference Number (URN)",
        "Customer organisation name",
        "Contract start date",
        "Contract end date",
        "Lot number",
        "Service Group",
        "Digital Marketplace Service ID",
        null,
        null,
        "Total contract value");

    private static readonly ContractReportFieldNames Vas = new(
        "Supplier Reference Number",
        "Customer Unique Reference Number (URN)",
        "Customer Organisation Name",
        "Contract Start Date",
        "Contract End Date",
        "Lot Number",
        null,
        null,
        "Product/Service Description",
        "Order Channel",
        "Total Contract Value");

    public static bool IsAvailable(FrameworkCode framework) => framework is
        FrameworkCode.GCloud13 or
        FrameworkCode.GCloud14 or
        FrameworkCode.VerticalApplicationSolutions;

    public static ContractReportFieldNames For(FrameworkCode framework) => framework switch
    {
        FrameworkCode.GCloud13 => GCloud13,
        FrameworkCode.GCloud14 => GCloud14,
        FrameworkCode.VerticalApplicationSolutions => Vas,
        _ => throw new InvalidOperationException($"No approved contract report template is available for {Frameworks.Get(framework).DisplayName}."),
    };
}
