using Remi.Domain;

namespace Remi.Application;

public sealed record DashboardModel(
    IReadOnlyList<FrameworkSummary> Frameworks,
    IReadOnlyList<ContractProgress> ContractProgress,
    IReadOnlyList<ValidationFinding> Findings,
    IReadOnlyList<AttentionItem> AttentionItems,
    string CurrentReportingMonth,
    IReadOnlyList<FrameworkReadiness> FrameworkReadiness,
    IReadOnlyList<AuditEventSummary> RecentActivity);

public sealed record AttentionItem(ValidationFinding Finding, string Route);

public sealed record FrameworkSummary(
    FrameworkDefinition Framework,
    int ContractCount,
    int InvoiceCount,
    int SubmittedReturnCount,
    int DraftReturnCount,
    int NilReturnCount,
    ReturnStatus? CurrentReturnStatus);

/// <summary>
/// The current reporting-period workload and validation state for one framework.
/// </summary>
public sealed record FrameworkReadiness(
    FrameworkDefinition Framework,
    int ContractCount,
    int InvoiceCount,
    ReturnStatus? ReturnStatus,
    int BlockingFindingCount,
    int ReviewFindingCount);

/// <summary>
/// The reporting start date configured for a framework Remi currently supports.
/// </summary>
public sealed record FrameworkConfigurationSummary(
    FrameworkDefinition Framework,
    DateOnly? StartDate);

public sealed record FrameworkConfigurationUpdateResult(
    bool Succeeded,
    string Message,
    FrameworkConfigurationSummary? Configuration);

public sealed record DigitalMarketplaceServiceUpdateResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<DigitalMarketplaceService> Services);

/// <summary>
/// A register view of every framework's reporting cycle, including cycles that are ready to start.
/// </summary>
public sealed record MonthlyReturnRegisterModel(
    IReadOnlyList<string> ReportingMonths,
    IReadOnlyList<MonthlyReturnRegisterEntry> Entries);

/// <summary>
/// The report lifecycle presented to users. A persisted <see cref="ReturnStatus.NilReturn"/>
/// remains a submitted report with an additional nil-return content indicator.
/// </summary>
public enum ReportLifecycleStatus
{
    Draft,
    Submitted,
    CorrectionRequired,
}

public sealed record MonthlyReturnRegisterEntry(
    FrameworkDefinition Framework,
    string ReportingMonth,
    ReportLifecycleStatus LifecycleStatus,
    bool IsNilReturn,
    int ContractCount,
    decimal ContractTotalExVat,
    int InvoiceCount,
    decimal InvoiceTotalExVat,
    int BlockingFindingCount,
    int ReviewFindingCount,
    DateTimeOffset? SubmittedAtUtc,
    DateOnly? InferredSubmissionDeadline,
    string? SubmissionReference,
    string? OriginalWorkbookName,
    DateTimeOffset? UpdatedAtUtc,
    Guid? ReturnId = null);

public sealed record ReturnSubmissionHistoryItem(
    DateTimeOffset OccurredAtUtc,
    bool IsNilReturn,
    string? SubmissionReference);

public sealed record ContractProgress(
    Guid ContractId,
    FrameworkCode Framework,
    string SupplierReference,
    string CustomerName,
    string? CustomerUrn,
    string ReportMonth,
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

/// <summary>
/// A registered contract that can provide the shared and framework-specific context for a new invoice.
/// </summary>
public sealed record InvoiceRegistrationContract(
    Guid ContractId,
    FrameworkCode Framework,
    string SupplierReference,
    string CustomerName,
    string? CustomerUrn,
    string? LotNumber,
    string? ServiceGroup,
    string? ServiceGroupLevel2,
    string? ServiceDescription,
    string? OrderChannel,
    string? DigitalMarketplaceServiceId,
    IReadOnlyList<ContractChangeRecord> AgreedChanges,
    decimal RemainingCommittedValueExVat,
    int UnconfirmedChangeCount);

/// <summary>
/// Values suggested for a new invoice. The most recently recorded invoice for the contract takes
/// precedence, with the contract record and standard MI defaults filling any missing value. The
/// invoice form still shows these values for review.
/// </summary>
public sealed record InvoiceReportingSuggestion(
    string CustomerName,
    string CustomerUrn,
    string LotNumber,
    string ServiceGroup,
    string ServiceGroupLevel2,
    string ServiceDescription,
    string OrderChannel,
    string DigitalMarketplaceServiceId,
    string UnitOfMeasure,
    decimal Quantity,
    string OriginalVendor,
    string SubcontractorName);

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

public sealed record HistoricalWorkbookImportResult(
    int NewContracts,
    int ExistingContracts,
    int NewInvoices,
    int ExistingInvoices,
    bool EvidenceArchived,
    IReadOnlyList<ValidationFinding> Findings);

/// <summary>
/// Identifies a framework and reporting month from a historical source-data import.
/// </summary>
public sealed record HistoricalReturnPeriod(
    FrameworkCode Framework,
    string ReportingMonth);

public sealed record ReturnActionResult(bool Succeeded, string Message, IReadOnlyList<ValidationFinding> Findings, Guid? EntityId = null);

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
    string SourceDescription,
    ContractPaymentPlanEntry? PaymentPlan = null);

/// <summary>
/// A structured annual payment plan captured during contract registration.
/// </summary>
public sealed record ContractPaymentPlanEntry(
    int BaseTermYears,
    int OptionalExtensionYears,
    IReadOnlyList<ContractPaymentPositionEntry> Positions);

public sealed record ContractPaymentPositionEntry(
    int ContractYear,
    string Description,
    decimal ValueExVat);

/// <summary>
/// Contract data recovered from a Ledger contract cell and its Excel comment. The Ledger file is
/// intentionally not retained as evidence; its source location and original notation are recorded
/// in the contract audit trail instead.
/// </summary>
public sealed record LedgerContractScheduleEntry(
    FrameworkCode Framework,
    string SupplierReference,
    string? CustomerName,
    string? CustomerUrn,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? LotNumber,
    string? ServiceGroup,
    string? DigitalMarketplaceServiceId,
    decimal? TotalContractValueExVat,
    string ReportingMonth,
    string SheetName,
    string CellAddress,
    ContractPaymentSchedule PaymentSchedule);

public sealed record LedgerScheduleImportResult(
    int ContractsCreated,
    int ContractsSupplemented,
    int PaymentPositionsAdded,
    IReadOnlyList<ValidationFinding> Findings);

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
    string SourceDescription,
    Guid? ContractChangeId = null);

/// <summary>
/// A reportable agreement to extend or vary an existing contract.  The report month is derived
/// from AgreementDate, never from the effective start date.
/// </summary>
public sealed record ContractChangeEntry(
    Guid ContractId,
    ContractChangeKind Kind,
    DateOnly? AgreementDate,
    DateOnly? EffectiveStartDate,
    DateOnly? EffectiveEndDate,
    decimal IncrementalValueExVat,
    bool WasProvidedForInOriginalCallOff,
    bool IsConfirmed,
    string? Reference);

public sealed record ChargeScheduleEntry(
    Guid ContractId,
    int ContractYear,
    string Description,
    DateOnly? ExpectedInvoiceDate,
    decimal ValueExVat,
    bool IsOptionalExtension = false);

public sealed record TemplateConfigurationSummary(
    Guid Id,
    FrameworkCode Framework,
    string WorkbookName,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc);

public sealed record TemplateRegistrationResult(bool Succeeded, string Message, TemplateConfigurationSummary? Template, IReadOnlyList<ValidationFinding> Findings);

public sealed record ExportedReturn(
    Guid EvidenceId,
    string FileName,
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
    IReadOnlyList<ContractChangeRecord> ContractChanges,
    IReadOnlyList<EvidenceLink> Evidence,
    IReadOnlyList<ValidationFinding> Findings);

/// <summary>
/// The complete register view for one invoice, including its linked contract when one exists.
/// </summary>
public sealed record InvoiceDetailsModel(
    InvoiceRecord Invoice,
    ContractRecord? Contract,
    ContractChangeRecord? ContractChange,
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
    string SourceWorkbook,
    IReadOnlyList<ValidationFinding> Findings);

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
    IReadOnlyList<ValidationFinding> Findings,
    int LedgerPaymentPositions,
    int SubmittedReturnReports,
    int InferredNilReturns);
