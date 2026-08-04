using Remi.Domain;

namespace Remi.Application;

public sealed record DashboardModel(
    IReadOnlyList<FrameworkSummary> Frameworks,
    IReadOnlyList<ContractProgress> ContractProgress,
    IReadOnlyList<ValidationFinding> Findings,
    IReadOnlyList<AttentionItem> AttentionItems,
    string CurrentReportingMonth);

public sealed record AttentionItem(ValidationFinding Finding, string Route);

public sealed record FrameworkSummary(
    FrameworkDefinition Framework,
    int ContractCount,
    int InvoiceCount,
    int SubmittedReturnCount,
    int DraftReturnCount,
    int NilReturnCount,
    ReturnStatus? CurrentReturnStatus);

public sealed record ContractProgress(
    Guid ContractId,
    FrameworkCode Framework,
    string SupplierReference,
    string CustomerName,
    string? CustomerUrn,
    DateOnly? EndDate,
    string? LotNumber,
    string? ServiceGroup,
    string? ServiceGroupLevel2,
    string? ServiceDescription,
    string? OrderChannel,
    string? DigitalMarketplaceServiceId,
    decimal TotalContractValueExVat,
    int ReportedInvoiceCount,
    decimal ReportedInvoiceValueExVat,
    decimal ComparisonValueExVat,
    bool UsesInvoicePlan,
    decimal CompletionRatio,
    IReadOnlyList<ChargeScheduleItem> ChargeSchedule,
    IReadOnlyList<EvidenceLink> Evidence);

public sealed record EvidenceLink(
    Guid Id,
    EvidenceKind Kind,
    string FileName,
    string OriginalRelativePath,
    string ContentType,
    string? ReportMonth,
    DateTimeOffset ArchivedAtUtc);

public sealed record ReportingEvidence(
    Guid Id,
    EvidenceKind Kind,
    string FileName,
    string OriginalRelativePath,
    string ContentType,
    long FileSizeBytes,
    string? ContractReference,
    DateTimeOffset ArchivedAtUtc);

public sealed record WorkbookImportResult(
    int NewContracts,
    int ExistingContracts,
    int NewInvoices,
    int ExistingInvoices,
    bool EvidenceArchived,
    IReadOnlyList<ValidationFinding> Findings);

public sealed record ReturnActionResult(bool Succeeded, string Message, IReadOnlyList<ValidationFinding> Findings);

public sealed record ContractEntry(
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
    string SourceDescription);

public sealed record InvoiceEntry(
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
    string SourceDescription);

public sealed record ChargeScheduleEntry(
    Guid ContractId,
    int ContractYear,
    string Description,
    DateOnly? ExpectedInvoiceDate,
    decimal ValueExVat);

public sealed record TemplateConfigurationSummary(
    Guid Id,
    FrameworkCode Framework,
    string Version,
    string WorkbookName,
    string GuidanceUrl,
    string? Notes,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc);

public sealed record TemplateRegistrationResult(bool Succeeded, string Message, TemplateConfigurationSummary? Template, IReadOnlyList<ValidationFinding> Findings);

public sealed record ExportedReturn(
    Guid EvidenceId,
    string FileName,
    string TemplateVersion,
    IReadOnlyList<ValidationFinding> Findings);

public sealed record AuditEventSummary(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string Action,
    string EntityType,
    string Summary,
    string? Reason,
    string Actor);

/// <summary>
/// A human-readable field in the same order as the official framework workbook.
/// </summary>
public sealed record ReportingCardField(string Label, string Value);

public sealed record ReportingCardItem(
    string Title,
    IReadOnlyList<ReportingCardField> Fields);

/// <summary>
/// The generated replacement for the manual MI Reporting Information Card text file.
/// </summary>
public sealed record ReportingCardModel(
    FrameworkDefinition Framework,
    string ReportingMonth,
    IReadOnlyList<ReportingCardItem> Contracts,
    IReadOnlyList<ReportingCardItem> Invoices);

/// <summary>
/// The complete register view for one contract, including related invoices, evidence and findings.
/// </summary>
public sealed record ContractDetailsModel(
    ContractRecord Contract,
    IReadOnlyList<InvoiceRecord> Invoices,
    IReadOnlyList<ChargeScheduleItem> ChargeSchedule,
    IReadOnlyList<EvidenceLink> Evidence,
    IReadOnlyList<ValidationFinding> Findings);

/// <summary>
/// The complete register view for one invoice, including its linked contract when one exists.
/// </summary>
public sealed record InvoiceDetailsModel(
    InvoiceRecord Invoice,
    ContractRecord? Contract,
    IReadOnlyList<EvidenceLink> Evidence,
    IReadOnlyList<ValidationFinding> Findings);

/// <summary>
/// A concise invoice row for the reporting register.
/// </summary>
public sealed record InvoiceRegisterItem(
    Guid InvoiceId,
    FrameworkCode Framework,
    string SupplierReference,
    string CustomerName,
    string InvoiceNumber,
    DateOnly? InvoiceDate,
    decimal TotalCostExVat,
    string ReportMonth,
    int EvidenceCount,
    bool HasMatchingContract,
    string SourceWorkbook);

/// <summary>
/// A read-only preview of a source-data folder before importing it into Remi.
/// </summary>
public sealed record MigrationPlan(
    string SourceDirectory,
    int SourceFileCount,
    int MiWorkbookCount,
    int RecognisedMiWorkbookCount,
    int SupportingEvidenceCount,
    IReadOnlyList<MigrationWorkbookPlan> Workbooks);

public sealed record MigrationWorkbookPlan(
    string RelativePath,
    FrameworkCode Framework,
    string ReportingMonth);

/// <summary>
/// The outcome of a no-write validation pass or an import into the local SQLite register.
/// </summary>
public sealed record MigrationReport(
    MigrationPlan Plan,
    bool DataWritten,
    bool ExistingDataReplaced,
    int ImportedContracts,
    int ExistingContracts,
    int ImportedInvoices,
    int ExistingInvoices,
    int ArchivedEvidenceFiles,
    IReadOnlyList<ValidationFinding> Findings);
