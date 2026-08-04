using Remi.Domain;

namespace Remi.Application;

public interface IWorkbookImporter
{
    Task<ImportedWorkbook> ImportAsync(
        FrameworkCode framework,
        string workbookName,
        Stream workbook,
        CancellationToken cancellationToken = default);
}

public sealed record ImportedWorkbook(
    string WorkbookName,
    IReadOnlyList<ImportedContract> Contracts,
    IReadOnlyList<ImportedInvoice> Invoices);

public sealed record ImportedContract(
    string SupplierReference,
    string CustomerName,
    string? CustomerUrn,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? LotNumber,
    string? ServiceGroup,
    string? ServiceGroupLevel2,
    string? ServiceDescription,
    string? OrderChannel,
    string? DigitalMarketplaceServiceId,
    decimal TotalContractValueExVat);

public sealed record ImportedInvoice(
    string SupplierReference,
    string CustomerName,
    string? CustomerUrn,
    DateOnly? InvoiceDate,
    string InvoiceNumber,
    string? LotNumber,
    string? ServiceGroup,
    string? ServiceGroupLevel2,
    string? ServiceDescription,
    string? DigitalMarketplaceServiceId,
    string? UnitOfMeasure,
    decimal? Quantity,
    decimal? PricePerUnitExVat,
    decimal TotalCostExVat,
    string? OriginalVendor,
    string? SubcontractorName);
