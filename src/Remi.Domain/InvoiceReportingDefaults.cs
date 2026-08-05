namespace Remi.Domain;

/// <summary>
/// Standard reporting values for invoices where the service is reported as a single unit.
/// </summary>
public static class InvoiceReportingDefaults
{
    public const string UnitOfMeasure = "Per unit";

    public const decimal Quantity = 1m;

    public static decimal PricePerUnitExVat(decimal totalCostExVat) => totalCostExVat;
}
