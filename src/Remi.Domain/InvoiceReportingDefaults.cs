namespace Remi.Domain;

/// <summary>
/// Standard reporting values for invoices where the service is reported as a single unit.
/// </summary>
public static class InvoiceReportingDefaults
{
    // Match the capitalisation of the values accepted by the G-Cloud MI templates.
    public const string UnitOfMeasure = "Per Unit";

    public const decimal Quantity = 1m;

    public const string OriginalVendor = "StatMap Ltd";

    public const string SubcontractorName = "N/A";

    public static decimal PricePerUnitExVat(decimal totalCostExVat) => totalCostExVat;
}
