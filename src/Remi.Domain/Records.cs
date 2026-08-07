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
    TemplateWorkbook,
    GeneratedMiWorkbook,
    ContractDocument,
    SupportingDocument,
    CustomerUrnList,
    SubmissionEvidence,
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
    string? OrderChannel,
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

/// <summary>
/// A separately agreed change to an existing call-off.  Options in a call-off's payment
/// schedule are not changes: they remain non-reportable until an agreement is recorded here.
/// </summary>
public enum ContractChangeKind
{
    Extension,
    Variation,
}

public sealed record ContractChangeRecord(
    Guid Id,
    Guid ContractId,
    ContractChangeKind Kind,
    DateOnly AgreementDate,
    DateOnly? EffectiveStartDate,
    DateOnly? EffectiveEndDate,
    decimal IncrementalValueExVat,
    bool WasProvidedForInOriginalCallOff,
    bool IsConfirmed,
    string? Reference,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Keeps an invoice's MI fields on the invoice while retaining which agreed contract change it
/// relates to for audit and payment-position calculations.
/// </summary>
public sealed record InvoiceContractChangeLink(Guid InvoiceId, Guid ContractChangeId);

public sealed record InvoicePlanItem(
    Guid Id,
    Guid ContractId,
    string Label,
    DateOnly? ExpectedInvoiceDate,
    decimal ExpectedValueExVat);

/// <summary>
/// A chargeable position in a contract year. Several positions can be retained for a year,
/// for example implementation, training and annual software licences.
/// </summary>
public sealed record ChargeScheduleItem(
    Guid Id,
    Guid ContractId,
    int ContractYear,
    string Description,
    DateOnly? ExpectedInvoiceDate,
    decimal ValueExVat,
    bool IsOptionalExtension,
    DateTimeOffset CreatedAtUtc);

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

/// <summary>
/// A registered, approved official workbook used as the immutable base for a generated MI return.
/// </summary>
public sealed record MiTemplateConfiguration(
    Guid Id,
    FrameworkCode Framework,
    Guid EvidenceId,
    string WorkbookName,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc);

/// <summary>
/// A local override of the date on which an existing supported framework enters Remi reporting.
/// </summary>
public sealed record FrameworkConfiguration(
    FrameworkCode Framework,
    DateOnly StartDate);

/// <summary>
/// A supplier service that Remi offers as a Digital Marketplace service-ID suggestion.
/// </summary>
public sealed record DigitalMarketplaceService(
    string ServiceId,
    string Name);

/// <summary>
/// An append-only record of material reporting actions and corrections.
/// </summary>
public sealed record AuditEvent(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string Action,
    string EntityType,
    Guid? EntityId,
    string Summary,
    string? Reason,
    string Actor);

public sealed class RemiDatabase
{
    public List<ContractRecord> Contracts { get; init; } = [];

    public List<InvoiceRecord> Invoices { get; init; } = [];

    public List<ContractChangeRecord> ContractChanges { get; init; } = [];

    public List<InvoiceContractChangeLink> InvoiceContractChangeLinks { get; init; } = [];

    public List<InvoicePlanItem> InvoicePlanItems { get; init; } = [];

    public List<ChargeScheduleItem> ChargeScheduleItems { get; init; } = [];

    public List<MonthlyReturn> MonthlyReturns { get; init; } = [];

    public List<EvidenceRecord> Evidence { get; init; } = [];

    public List<MiTemplateConfiguration> MiTemplates { get; init; } = [];

    public List<FrameworkConfiguration> FrameworkConfigurations { get; init; } = [];

    public List<DigitalMarketplaceService> DigitalMarketplaceServices { get; init; } = [];

    public List<AuditEvent> AuditEvents { get; init; } = [];
}
