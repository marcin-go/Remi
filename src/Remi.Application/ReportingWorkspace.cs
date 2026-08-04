using Remi.Domain;

namespace Remi.Application;

public sealed class ReportingWorkspace(
    IRemiStore store,
    IWorkbookImporter workbookImporter,
    IEvidenceArchive evidenceArchive,
    TimeProvider timeProvider)
{
    public Task<DashboardModel> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => BuildDashboard(database, Today()), cancellationToken);

    public async Task<WorkbookImportResult> ImportWorkbookAsync(
        FrameworkCode framework,
        string reportingMonth,
        string workbookName,
        Stream workbook,
        string? originalRelativePath = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(reportingMonth);
        await using var copiedWorkbook = new MemoryStream();
        await workbook.CopyToAsync(copiedWorkbook, cancellationToken);
        copiedWorkbook.Position = 0;
        var imported = await workbookImporter.ImportAsync(framework, workbookName, copiedWorkbook, cancellationToken);
        copiedWorkbook.Position = 0;
        var archivedFile = await evidenceArchive.ArchiveAsync(
            new EvidenceArchiveRequest(
                workbookName,
                originalRelativePath ?? workbookName,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                copiedWorkbook),
            cancellationToken);

        return await store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            var evidenceArchived = AddEvidence(
                database,
                new EvidenceRecord(
                    Guid.NewGuid(),
                    EvidenceKind.MonthlyMiWorkbook,
                    framework,
                    reportingMonth,
                    workbookName,
                    originalRelativePath ?? workbookName,
                    archivedFile.StoredRelativePath,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    archivedFile.FileSizeBytes,
                    archivedFile.Sha256,
                    null,
                    now));
            var existingContractReferences = database.Contracts
                .Select(contract => (contract.Framework, ReportingRules.NormaliseReference(contract.SupplierReference)))
                .ToHashSet();
            var existingInvoices = database.Invoices
                .Select(invoice => InvoiceKey(invoice.Framework, invoice.SupplierReference, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.TotalCostExVat))
                .ToHashSet();

            var newContracts = 0;
            var existingContracts = 0;
            foreach (var contract in imported.Contracts)
            {
                var key = (framework, ReportingRules.NormaliseReference(contract.SupplierReference));
                if (!existingContractReferences.Add(key))
                {
                    existingContracts++;
                    continue;
                }

                database.Contracts.Add(new ContractRecord(
                    Guid.NewGuid(),
                    framework,
                    contract.SupplierReference,
                    contract.CustomerName,
                    contract.CustomerUrn,
                    contract.StartDate,
                    contract.EndDate,
                    contract.LotNumber,
                    contract.ServiceGroup,
                    contract.ServiceGroupLevel2,
                    contract.ServiceDescription,
                    contract.OrderChannel,
                    contract.DigitalMarketplaceServiceId,
                    contract.TotalContractValueExVat,
                    reportingMonth,
                    imported.WorkbookName,
                    now));
                newContracts++;
            }

            var newInvoices = 0;
            var existingInvoiceCount = 0;
            foreach (var invoice in imported.Invoices)
            {
                var key = InvoiceKey(framework, invoice.SupplierReference, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.TotalCostExVat);
                if (!existingInvoices.Add(key))
                {
                    existingInvoiceCount++;
                    continue;
                }

                database.Invoices.Add(new InvoiceRecord(
                    Guid.NewGuid(),
                    framework,
                    invoice.SupplierReference,
                    invoice.CustomerName,
                    invoice.CustomerUrn,
                    invoice.InvoiceDate,
                    invoice.InvoiceNumber,
                    invoice.LotNumber,
                    invoice.ServiceGroup,
                    invoice.ServiceGroupLevel2,
                    invoice.ServiceDescription,
                    invoice.DigitalMarketplaceServiceId,
                    invoice.UnitOfMeasure,
                    invoice.Quantity,
                    invoice.PricePerUnitExVat,
                    invoice.TotalCostExVat,
                    invoice.OriginalVendor,
                    invoice.SubcontractorName,
                    reportingMonth,
                    imported.WorkbookName,
                    now));
                newInvoices++;
            }

            EnsureReturn(database, framework, reportingMonth, imported.WorkbookName, now);
            return new WorkbookImportResult(
                newContracts,
                existingContracts,
                newInvoices,
                existingInvoiceCount,
                evidenceArchived,
                ReportingRules.Validate(database));
        }, cancellationToken);
    }

    public async Task<bool> ArchiveEvidenceAsync(
        EvidenceKind kind,
        FrameworkCode? framework,
        string? reportingMonth,
        string fileName,
        string originalRelativePath,
        string contentType,
        string? contractReference,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (reportingMonth is not null)
        {
            ValidateReportingMonth(reportingMonth);
        }

        var archivedFile = await evidenceArchive.ArchiveAsync(
            new EvidenceArchiveRequest(fileName, originalRelativePath, contentType, content),
            cancellationToken);
        return await store.UpdateAsync(database => AddEvidence(
            database,
            new EvidenceRecord(
                Guid.NewGuid(),
                kind,
                framework,
                reportingMonth,
                fileName,
                originalRelativePath,
                archivedFile.StoredRelativePath,
                contentType,
                archivedFile.FileSizeBytes,
                archivedFile.Sha256,
                string.IsNullOrWhiteSpace(contractReference) ? null : contractReference.Trim(),
                timeProvider.GetUtcNow())), cancellationToken);
    }

    public Task<IReadOnlyList<ReportingEvidence>> GetReportingEvidenceAsync(
        FrameworkCode framework,
        string reportingMonth,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(reportingMonth);
        return store.ReadAsync(database => (IReadOnlyList<ReportingEvidence>)database.Evidence
            .Where(item => item.Framework == framework && item.ReportMonth == reportingMonth)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.OriginalRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(ToReportingEvidence)
            .ToList(), cancellationToken);
    }

    public Task<ReturnActionResult> MarkSubmittedAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? submissionReference,
        CancellationToken cancellationToken = default) =>
        UpdateReturnAsync(framework, reportingMonth, ReturnStatus.Submitted, submissionReference, cancellationToken);

    public Task<ReturnActionResult> MarkNilReturnAsync(
        FrameworkCode framework,
        string reportingMonth,
        CancellationToken cancellationToken = default) =>
        UpdateReturnAsync(framework, reportingMonth, ReturnStatus.NilReturn, null, cancellationToken);

    private Task<ReturnActionResult> UpdateReturnAsync(
        FrameworkCode framework,
        string reportingMonth,
        ReturnStatus status,
        string? submissionReference,
        CancellationToken cancellationToken)
    {
        ValidateReportingMonth(reportingMonth);
        return store.UpdateAsync(database =>
        {
            var findings = ReportingRules.Validate(database);
            var periodFindings = findings.Where(finding =>
                finding.Severity == FindingSeverity.Error &&
                IsInReportingPeriod(database, finding, framework, reportingMonth)).ToList();

            if (periodFindings.Count != 0)
            {
                return new ReturnActionResult(false, "Resolve the errors for this return before recording it as submitted.", periodFindings);
            }

            var hasActivity = database.Contracts.Any(contract => contract.Framework == framework && contract.ReportMonth == reportingMonth)
                || database.Invoices.Any(invoice => invoice.Framework == framework && invoice.ReportMonth == reportingMonth);
            if (status == ReturnStatus.NilReturn && hasActivity)
            {
                return new ReturnActionResult(false, "This reporting period has imported activity and cannot be recorded as a nil return.", []);
            }

            var now = timeProvider.GetUtcNow();
            var existing = EnsureReturn(database, framework, reportingMonth, null, now);
            var replacement = existing with
            {
                Status = status,
                SubmittedAtUtc = status == ReturnStatus.Submitted ? now : null,
                SubmissionReference = string.IsNullOrWhiteSpace(submissionReference) ? null : submissionReference.Trim(),
                UpdatedAtUtc = now,
            };
            database.MonthlyReturns[database.MonthlyReturns.FindIndex(item => item.Id == existing.Id)] = replacement;

            var message = status == ReturnStatus.NilReturn
                ? "The nil return has been recorded."
                : "The return has been recorded as submitted.";
            return new ReturnActionResult(true, message, []);
        }, cancellationToken);
    }

    private DashboardModel BuildDashboard(RemiDatabase database, DateOnly today)
    {
        var currentReportingMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1).ToString("yyyy-MM");
        var summaries = Frameworks.All.Select(framework => new FrameworkSummary(
            framework,
            database.Contracts.Count(contract => contract.Framework == framework.Code),
            database.Invoices.Count(invoice => invoice.Framework == framework.Code),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.Submitted),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.Draft),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.NilReturn),
            database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework.Code && item.ReportMonth == currentReportingMonth)?.Status)).ToList();

        var progress = database.Contracts.Select(contract =>
        {
            var reportedInvoiceValue = database.Invoices
                .Where(invoice => invoice.Framework == contract.Framework &&
                    ReportingRules.NormaliseReference(invoice.SupplierReference) == ReportingRules.NormaliseReference(contract.SupplierReference))
                .Sum(invoice => invoice.TotalCostExVat);
            var invoicePlanValue = database.InvoicePlanItems
                .Where(item => item.ContractId == contract.Id)
                .Sum(item => item.ExpectedValueExVat);
            var comparisonValue = invoicePlanValue > 0 ? invoicePlanValue : contract.TotalContractValueExVat;
            var evidence = database.Evidence
                .Where(item => item.Framework == contract.Framework &&
                    string.Equals(
                        ReportingRules.NormaliseReference(item.ContractReference ?? string.Empty),
                        ReportingRules.NormaliseReference(contract.SupplierReference),
                        StringComparison.Ordinal))
                .OrderBy(item => item.OriginalRelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(ToEvidenceLink)
                .ToList();
            return new ContractProgress(
                contract.Id,
                contract.Framework,
                contract.SupplierReference,
                contract.CustomerName,
                contract.EndDate,
                contract.TotalContractValueExVat,
                reportedInvoiceValue,
                comparisonValue,
                invoicePlanValue > 0,
                comparisonValue == 0 ? 0 : reportedInvoiceValue / comparisonValue,
                evidence);
        })
        .OrderBy(item => item.CompletionRatio)
        .ThenBy(item => item.EndDate)
        .ToList();

        return new DashboardModel(summaries, progress, ReportingRules.Validate(database), currentReportingMonth);
    }

    private MonthlyReturn EnsureReturn(
        RemiDatabase database,
        FrameworkCode framework,
        string reportingMonth,
        string? workbookName,
        DateTimeOffset now)
    {
        var existing = database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework && item.ReportMonth == reportingMonth);
        if (existing is not null)
        {
            return existing;
        }

        var created = new MonthlyReturn(Guid.NewGuid(), framework, reportingMonth, ReturnStatus.Draft, null, null, workbookName, now);
        database.MonthlyReturns.Add(created);
        return created;
    }

    private static bool IsInReportingPeriod(
        RemiDatabase database,
        ValidationFinding finding,
        FrameworkCode framework,
        string reportingMonth)
    {
        return database.Contracts.Any(contract => contract.Id == finding.EntityId && contract.Framework == framework && contract.ReportMonth == reportingMonth)
            || database.Invoices.Any(invoice => invoice.Id == finding.EntityId && invoice.Framework == framework && invoice.ReportMonth == reportingMonth);
    }

    private static string InvoiceKey(FrameworkCode framework, string supplierReference, string invoiceNumber, DateOnly? invoiceDate, decimal total) =>
        $"{framework}|{ReportingRules.NormaliseReference(supplierReference)}|{invoiceNumber.Trim()}|{invoiceDate:yyyy-MM-dd}|{total}";

    private static bool AddEvidence(RemiDatabase database, EvidenceRecord evidence)
    {
        var alreadyRecorded = database.Evidence.Any(item =>
            string.Equals(item.OriginalRelativePath, evidence.OriginalRelativePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Sha256, evidence.Sha256, StringComparison.OrdinalIgnoreCase));
        if (alreadyRecorded)
        {
            return false;
        }

        database.Evidence.Add(evidence);
        return true;
    }

    private static EvidenceLink ToEvidenceLink(EvidenceRecord evidence) => new(
        evidence.Id,
        evidence.Kind,
        evidence.FileName,
        evidence.OriginalRelativePath,
        evidence.ContentType,
        evidence.ReportMonth,
        evidence.ArchivedAtUtc);

    private static ReportingEvidence ToReportingEvidence(EvidenceRecord evidence) => new(
        evidence.Id,
        evidence.Kind,
        evidence.FileName,
        evidence.OriginalRelativePath,
        evidence.ContentType,
        evidence.FileSizeBytes,
        evidence.ContractReference,
        evidence.ArchivedAtUtc);

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    private static void ValidateReportingMonth(string reportingMonth)
    {
        if (!DateOnly.TryParseExact($"{reportingMonth}-01", "yyyy-MM-dd", out _))
        {
            throw new ArgumentException("Reporting month must use the yyyy-MM format.", nameof(reportingMonth));
        }
    }
}
