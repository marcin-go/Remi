using Remi.Domain;

namespace Remi.Application;

public sealed record DashboardModel(
    IReadOnlyList<FrameworkSummary> Frameworks,
    IReadOnlyList<ContractProgress> ContractProgress,
    IReadOnlyList<ValidationFinding> Findings,
    string CurrentReportingMonth);

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
    DateOnly? EndDate,
    decimal TotalContractValueExVat,
    decimal ReportedInvoiceValueExVat,
    decimal ComparisonValueExVat,
    bool UsesInvoicePlan,
    decimal CompletionRatio,
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
