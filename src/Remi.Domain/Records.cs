namespace Remi.Domain;

public enum ReturnStatus
{
    Draft,
    Submitted,
    NilReturn,
    CorrectionRequired,
}

public enum FindingSeverity
{
    Warning,
    Error,
}

/// <summary>
/// Describes why an original source file is being retained in Remi's evidence archive.
/// </summary>
public enum EvidenceKind
{
    MonthlyMiWorkbook,
    ContractDocument,
    SupportingDocument,
}

public sealed record ContractRecord(
    Guid Id,
    FrameworkCode Framework,
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
    decimal TotalContractValueExVat,
    string ReportMonth,
    string SourceWorkbook,
    DateTimeOffset CreatedAtUtc);

public sealed record InvoiceRecord(
    Guid Id,
    FrameworkCode Framework,
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
    string? SubcontractorName,
    string ReportMonth,
    string SourceWorkbook,
    DateTimeOffset CreatedAtUtc);

public sealed record InvoicePlanItem(
    Guid Id,
    Guid ContractId,
    string Label,
    DateOnly? ExpectedInvoiceDate,
    decimal ExpectedValueExVat);

public sealed record MonthlyReturn(
    Guid Id,
    FrameworkCode Framework,
    string ReportMonth,
    ReturnStatus Status,
    DateTimeOffset? SubmittedAtUtc,
    string? SubmissionReference,
    string? OriginalWorkbookName,
    DateTimeOffset UpdatedAtUtc);

public sealed record ValidationFinding(
    FindingSeverity Severity,
    string Code,
    string Message,
    string EntityType,
    Guid? EntityId = null);

/// <summary>
/// Metadata for an immutable copy of a source file held alongside the portable Remi data.
/// </summary>
public sealed record EvidenceRecord(
    Guid Id,
    EvidenceKind Kind,
    FrameworkCode? Framework,
    string? ReportMonth,
    string FileName,
    string OriginalRelativePath,
    string StoredRelativePath,
    string ContentType,
    long FileSizeBytes,
    string Sha256,
    string? ContractReference,
    DateTimeOffset ArchivedAtUtc);

public sealed class RemiDatabase
{
    public int SchemaVersion { get; init; } = 1;

    public List<ContractRecord> Contracts { get; init; } = [];

    public List<InvoiceRecord> Invoices { get; init; } = [];

    public List<InvoicePlanItem> InvoicePlanItems { get; init; } = [];

    public List<MonthlyReturn> MonthlyReturns { get; init; } = [];

    public List<EvidenceRecord> Evidence { get; init; } = [];
}
