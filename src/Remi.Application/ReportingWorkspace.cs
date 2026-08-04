using System.Text;
using Remi.Domain;

namespace Remi.Application;

public sealed class ReportingWorkspace(
    IRemiStore store,
    IWorkbookImporter workbookImporter,
    IMiWorkbookExporter workbookExporter,
    IEvidenceArchive evidenceArchive,
    ICustomerUrnDirectory customerUrnDirectory,
    TimeProvider timeProvider)
{
    public Task<DashboardModel> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        GetDashboardAsync(null, cancellationToken);

    public Task<DashboardModel> GetDashboardAsync(
        string? reportingMonth,
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => BuildDashboard(database, Today(), reportingMonth), cancellationToken);

    public Task<IReadOnlyList<string>> GetReportingPeriodsAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => (IReadOnlyList<string>)database.Contracts
            .Select(contract => contract.ReportMonth)
            .Concat(database.Invoices.Select(invoice => invoice.ReportMonth))
            .Concat(database.MonthlyReturns.Select(monthlyReturn => monthlyReturn.ReportMonth))
            .Where(IsValidReportingMonth)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(month => month, StringComparer.Ordinal)
            .ToList(), cancellationToken);

    public Task<CustomerUrnDirectoryStatus?> GetCustomerUrnDirectoryStatusAsync(
        CancellationToken cancellationToken = default) =>
        customerUrnDirectory.GetStatusAsync(cancellationToken);

    public Task<IReadOnlyList<CustomerUrnSuggestion>> SearchCustomerUrnsAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        customerUrnDirectory.SearchAsync(query, cancellationToken: cancellationToken);

    public async Task<CustomerUrnDirectoryStatus> RefreshCustomerUrnDirectoryAsync(
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var evidenceId = Guid.NewGuid();
        var refreshed = await customerUrnDirectory.RefreshAsync(evidenceId, cancellationToken);
        return await store.UpdateAsync(database =>
        {
            database.Evidence.Add(new EvidenceRecord(
                evidenceId,
                EvidenceKind.CustomerUrnList,
                null,
                null,
                refreshed.Status.FileName,
                refreshed.OriginalRelativePath,
                refreshed.ArchivedFile.StoredRelativePath,
                "application/vnd.oasis.opendocument.spreadsheet",
                refreshed.ArchivedFile.FileSizeBytes,
                refreshed.ArchivedFile.Sha256,
                null,
                refreshed.Status.DownloadedAtUtc));
            RecordAudit(
                database,
                refreshed.Status.DownloadedAtUtc,
                "CustomerUrnListRefreshed",
                "CustomerUrnList",
                evidenceId,
                $"Refreshed the customer URN list with {refreshed.Status.OrganisationCount:N0} organisation(s).",
                $"Source page: {refreshed.Status.SourcePageUrl}; resolved ODS: {refreshed.Status.ResolvedDownloadUrl}.",
                actor);
            return refreshed.Status;
        }, cancellationToken);
    }

    public Task<ContractDetailsModel?> GetContractDetailsAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (contract is null)
            {
                return null;
            }

            var invoices = database.Invoices
                .Where(item => item.Framework == contract.Framework &&
                    ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(contract.SupplierReference))
                .OrderByDescending(item => item.InvoiceDate)
                .ThenByDescending(item => item.CreatedAtUtc)
                .ToList();
            var chargeSchedule = database.ChargeScheduleItems
                .Where(item => item.ContractId == contract.Id)
                .OrderBy(item => item.ContractYear)
                .ThenByDescending(item => item.ValueExVat)
                .ThenBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var findings = ReportingRules.Validate(database)
                .Where(item => item.EntityId == contract.Id ||
                    (item.EntityType == "ChargeSchedule" && database.ChargeScheduleItems.Any(schedule => schedule.Id == item.EntityId && schedule.ContractId == contract.Id)))
                .ToList();
            return new ContractDetailsModel(contract, invoices, chargeSchedule, EvidenceForContract(database, contract), findings);
        }, cancellationToken);

    public Task<InvoiceDetailsModel?> GetInvoiceDetailsAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var invoice = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            if (invoice is null)
            {
                return null;
            }

            var contract = database.Contracts.SingleOrDefault(item => item.Framework == invoice.Framework &&
                ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(invoice.SupplierReference));
            var findings = ReportingRules.Validate(database)
                .Where(item => item.EntityId == invoice.Id)
                .ToList();
            return new InvoiceDetailsModel(invoice, contract, EvidenceForInvoice(database, invoice), findings);
        }, cancellationToken);

    public Task<IReadOnlyList<InvoiceRegisterItem>> GetInvoiceRegisterAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => (IReadOnlyList<InvoiceRegisterItem>)database.Invoices
            .Select(invoice => new InvoiceRegisterItem(
                invoice.Id,
                invoice.Framework,
                invoice.SupplierReference,
                invoice.CustomerName,
                invoice.InvoiceNumber,
                invoice.InvoiceDate,
                invoice.TotalCostExVat,
                invoice.ReportMonth,
                EvidenceForInvoice(database, invoice).Count,
                database.Contracts.Any(contract =>
                    contract.Framework == invoice.Framework &&
                    ReportingRules.NormaliseReference(contract.SupplierReference) == ReportingRules.NormaliseReference(invoice.SupplierReference)),
                invoice.SourceWorkbook))
            .OrderByDescending(item => item.InvoiceDate)
            .ThenBy(item => item.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken);

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
            if (evidenceArchived)
            {
                RecordAudit(database, now, "WorkbookImported", "MonthlyReturn", null, $"Imported {workbookName} for {Frameworks.Get(framework).DisplayName} {reportingMonth}.", null);
            }
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
                RecordAudit(database, now, "ContractImported", "Contract", database.Contracts[^1].Id, $"Imported contract {contract.SupplierReference} from {workbookName}.", null);
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
                    invoice.OrderChannel,
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
                RecordAudit(database, now, "InvoiceImported", "Invoice", database.Invoices[^1].Id, $"Imported invoice {invoice.InvoiceNumber} for {invoice.SupplierReference} from {workbookName}.", null);
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
        return await store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            var archived = AddEvidence(
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
                    now));
            if (archived)
            {
                RecordAudit(database, now, "EvidenceArchived", "Evidence", database.Evidence[^1].Id, $"Archived {fileName}.", null);
            }

            return archived;
        }, cancellationToken);
    }

    public Task<ReturnActionResult> CreateContractAsync(
        ContractEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(entry.ReportMonth);
        if (!Frameworks.AllowsNewContracts(entry.Framework))
        {
            return Task.FromResult(new ReturnActionResult(
                false,
                $"{Frameworks.Get(entry.Framework).DisplayName} is reporting-only and cannot accept a new contract record.",
                []));
        }

        var paymentPlanError = ValidatePaymentPlan(entry.PaymentPlan);
        if (paymentPlanError is not null)
        {
            return Task.FromResult(new ReturnActionResult(false, paymentPlanError, []));
        }

        return store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            var record = new ContractRecord(
                Guid.NewGuid(),
                entry.Framework,
                entry.SupplierReference.Trim(),
                entry.CustomerName.Trim(),
                NullIfWhiteSpace(entry.CustomerUrn),
                entry.StartDate,
                entry.EndDate,
                NullIfWhiteSpace(entry.LotNumber),
                NullIfWhiteSpace(entry.ServiceGroup),
                NullIfWhiteSpace(entry.ServiceGroupLevel2),
                NullIfWhiteSpace(entry.ServiceDescription),
                NullIfWhiteSpace(entry.OrderChannel),
                NullIfWhiteSpace(entry.DigitalMarketplaceServiceId),
                entry.TotalContractValueExVat,
                entry.ReportMonth,
                string.IsNullOrWhiteSpace(entry.SourceDescription) ? "Manual entry" : entry.SourceDescription.Trim(),
                now);
            database.Contracts.Add(record);
            var findings = ReportingRules.Validate(database);
            var errors = findings.Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == record.Id).ToList();
            if (errors.Count != 0 || database.Contracts.Count(contract => contract.Framework == record.Framework && ReportingRules.NormaliseReference(contract.SupplierReference) == ReportingRules.NormaliseReference(record.SupplierReference)) > 1)
            {
                database.Contracts.Remove(record);
                return new ReturnActionResult(false, "The contract was not added. Resolve the highlighted fields first.", errors);
            }

            RecordAudit(database, now, "ContractCreated", "Contract", record.Id, $"Created contract {record.SupplierReference} for {record.CustomerName}.", null, actor);
            if (entry.PaymentPlan is { } paymentPlan)
            {
                AddManualPaymentScheduleItems(database, record, paymentPlan, now);
                RecordAudit(
                    database,
                    now,
                    "ContractPaymentScheduleRecorded",
                    "Contract",
                    record.Id,
                    $"Recorded {paymentPlan.Positions.Count} contract payment position(s) across a {PaymentPlanTerm(paymentPlan)} term.",
                    PaymentPlanSummary(paymentPlan),
                    actor);
            }

            EnsureReturn(database, entry.Framework, entry.ReportMonth, null, now);
            return new ReturnActionResult(true, "The contract has been added to the reporting register.", []);
        }, cancellationToken);
    }

    /// <summary>
    /// Supplements imported monthly-return contracts with Ledger contract details and payment
    /// positions. The Ledger itself remains outside the evidence archive by design.
    /// </summary>
    public Task<LedgerScheduleImportResult> ImportLedgerSchedulesAsync(
        IReadOnlyList<LedgerContractScheduleEntry> entries,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            var created = 0;
            var supplemented = 0;
            var positionsAdded = 0;

            foreach (var entry in entries)
            {
                var contract = database.Contracts.SingleOrDefault(item =>
                    item.Framework == entry.Framework &&
                    ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(entry.SupplierReference));

                if (contract is null)
                {
                    if (string.IsNullOrWhiteSpace(entry.CustomerName) || entry.TotalContractValueExVat is not > 0)
                    {
                        continue;
                    }

                    contract = new ContractRecord(
                        Guid.NewGuid(),
                        entry.Framework,
                        entry.SupplierReference.Trim(),
                        entry.CustomerName.Trim(),
                        NullIfWhiteSpace(entry.CustomerUrn),
                        entry.StartDate,
                        entry.EndDate,
                        NullIfWhiteSpace(entry.LotNumber),
                        NullIfWhiteSpace(entry.ServiceGroup),
                        null,
                        null,
                        null,
                        NullIfWhiteSpace(entry.DigitalMarketplaceServiceId),
                        entry.TotalContractValueExVat.Value,
                        entry.ReportingMonth,
                        $"MI Reporting Ledger.xlsx ({entry.SheetName}!{entry.CellAddress})",
                        now);
                    database.Contracts.Add(contract);
                    created++;
                    RecordAudit(database, now, "ContractMigratedFromLedger", "Contract", contract.Id, $"Created contract {contract.SupplierReference} from MI Reporting Ledger.xlsx ({entry.SheetName}!{entry.CellAddress}).", null, actor);
                }
                else
                {
                    var supplementedContract = MergeLedgerContractDetails(contract, entry);
                    if (supplementedContract != contract)
                    {
                        var index = database.Contracts.IndexOf(contract);
                        database.Contracts[index] = supplementedContract;
                        contract = supplementedContract;
                        supplemented++;
                    }
                }

                var scheduleUpdate = AddPaymentScheduleItems(database, contract, entry.PaymentSchedule, now);
                if (scheduleUpdate.Added == 0 && scheduleUpdate.Relabelled == 0)
                {
                    continue;
                }

                positionsAdded += scheduleUpdate.Added;
                var changeDescription = scheduleUpdate.Relabelled == 0
                    ? $"Imported {scheduleUpdate.Added} payment position(s)"
                    : $"Imported {scheduleUpdate.Added} payment position(s) and relabelled {scheduleUpdate.Relabelled}";
                RecordAudit(
                    database,
                    now,
                    "LedgerPaymentScheduleImported",
                    "Contract",
                    contract.Id,
                    $"{changeDescription} from MI Reporting Ledger.xlsx ({entry.SheetName}!{entry.CellAddress}).",
                    entry.PaymentSchedule.Notation,
                    actor);
            }

            return new LedgerScheduleImportResult(created, supplemented, positionsAdded, ReportingRules.Validate(database));
        }, cancellationToken);

    public Task<ReturnActionResult> RecordInvoiceAsync(
        InvoiceEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(entry.ReportMonth);
        return store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            var record = new InvoiceRecord(
                Guid.NewGuid(),
                entry.Framework,
                entry.SupplierReference.Trim(),
                entry.CustomerName.Trim(),
                NullIfWhiteSpace(entry.CustomerUrn),
                entry.InvoiceDate,
                entry.InvoiceNumber.Trim(),
                NullIfWhiteSpace(entry.LotNumber),
                NullIfWhiteSpace(entry.ServiceGroup),
                NullIfWhiteSpace(entry.ServiceGroupLevel2),
                NullIfWhiteSpace(entry.ServiceDescription),
                NullIfWhiteSpace(entry.OrderChannel),
                NullIfWhiteSpace(entry.DigitalMarketplaceServiceId),
                NullIfWhiteSpace(entry.UnitOfMeasure),
                entry.Quantity,
                entry.PricePerUnitExVat,
                entry.TotalCostExVat,
                NullIfWhiteSpace(entry.OriginalVendor),
                NullIfWhiteSpace(entry.SubcontractorName),
                entry.ReportMonth,
                string.IsNullOrWhiteSpace(entry.SourceDescription) ? "Manual entry" : entry.SourceDescription.Trim(),
                now);
            database.Invoices.Add(record);
            var findings = ReportingRules.Validate(database);
            var errors = findings.Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == record.Id).ToList();
            var duplicate = database.Invoices.Count(invoice => InvoiceKey(invoice.Framework, invoice.SupplierReference, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.TotalCostExVat) == InvoiceKey(record.Framework, record.SupplierReference, record.InvoiceNumber, record.InvoiceDate, record.TotalCostExVat)) > 1;
            if (errors.Count != 0 || duplicate)
            {
                database.Invoices.Remove(record);
                return new ReturnActionResult(false, "The invoice was not added. It must match a contract and not duplicate an existing invoice.", errors);
            }

            RecordAudit(database, now, "InvoiceRecorded", "Invoice", record.Id, $"Recorded invoice {record.InvoiceNumber} for {record.SupplierReference}.", null, actor);
            EnsureReturn(database, entry.Framework, entry.ReportMonth, null, now);
            return new ReturnActionResult(true, "The invoice has been recorded.", []);
        }, cancellationToken);
    }

    public Task<ReturnActionResult> UpdateContractAsync(
        Guid contractId,
        ContractEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(entry.ReportMonth);
        return store.UpdateAsync(database =>
        {
            var existing = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (existing is null)
            {
                return new ReturnActionResult(false, "The selected contract no longer exists.", []);
            }

            var updated = new ContractRecord(existing.Id, entry.Framework, entry.SupplierReference.Trim(), entry.CustomerName.Trim(), NullIfWhiteSpace(entry.CustomerUrn), entry.StartDate, entry.EndDate, NullIfWhiteSpace(entry.LotNumber), NullIfWhiteSpace(entry.ServiceGroup), NullIfWhiteSpace(entry.ServiceGroupLevel2), NullIfWhiteSpace(entry.ServiceDescription), NullIfWhiteSpace(entry.OrderChannel), NullIfWhiteSpace(entry.DigitalMarketplaceServiceId), entry.TotalContractValueExVat, entry.ReportMonth, existing.SourceWorkbook, existing.CreatedAtUtc);
            var index = database.Contracts.IndexOf(existing);
            database.Contracts[index] = updated;
            var findings = ReportingRules.Validate(database);
            var duplicate = database.Contracts.Any(contract => contract.Id != contractId && contract.Framework == updated.Framework && ReportingRules.NormaliseReference(contract.SupplierReference) == ReportingRules.NormaliseReference(updated.SupplierReference));
            var errors = findings.Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == contractId).ToList();
            if (errors.Count != 0 || duplicate)
            {
                database.Contracts[index] = existing;
                return new ReturnActionResult(false, "The contract was not updated. Resolve the highlighted fields first.", errors);
            }

            RecordAudit(database, timeProvider.GetUtcNow(), "ContractUpdated", "Contract", contractId, $"Updated contract {updated.SupplierReference}.", null, actor);
            return new ReturnActionResult(true, "The contract has been updated.", []);
        }, cancellationToken);
    }

    public Task<ReturnActionResult> UpdateInvoiceAsync(
        Guid invoiceId,
        InvoiceEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(entry.ReportMonth);
        return store.UpdateAsync(database =>
        {
            var existing = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            if (existing is null)
            {
                return new ReturnActionResult(false, "The selected invoice no longer exists.", []);
            }

            var updated = new InvoiceRecord(existing.Id, entry.Framework, entry.SupplierReference.Trim(), entry.CustomerName.Trim(), NullIfWhiteSpace(entry.CustomerUrn), entry.InvoiceDate, entry.InvoiceNumber.Trim(), NullIfWhiteSpace(entry.LotNumber), NullIfWhiteSpace(entry.ServiceGroup), NullIfWhiteSpace(entry.ServiceGroupLevel2), NullIfWhiteSpace(entry.ServiceDescription), NullIfWhiteSpace(entry.OrderChannel), NullIfWhiteSpace(entry.DigitalMarketplaceServiceId), NullIfWhiteSpace(entry.UnitOfMeasure), entry.Quantity, entry.PricePerUnitExVat, entry.TotalCostExVat, NullIfWhiteSpace(entry.OriginalVendor), NullIfWhiteSpace(entry.SubcontractorName), entry.ReportMonth, existing.SourceWorkbook, existing.CreatedAtUtc);
            var index = database.Invoices.IndexOf(existing);
            database.Invoices[index] = updated;
            var findings = ReportingRules.Validate(database);
            var duplicate = database.Invoices.Any(invoice => invoice.Id != invoiceId && InvoiceKey(invoice.Framework, invoice.SupplierReference, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.TotalCostExVat) == InvoiceKey(updated.Framework, updated.SupplierReference, updated.InvoiceNumber, updated.InvoiceDate, updated.TotalCostExVat));
            var errors = findings.Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == invoiceId).ToList();
            if (errors.Count != 0 || duplicate)
            {
                database.Invoices[index] = existing;
                return new ReturnActionResult(false, "The invoice was not updated. It must match a contract and not duplicate an existing invoice.", errors);
            }

            RecordAudit(database, timeProvider.GetUtcNow(), "InvoiceUpdated", "Invoice", invoiceId, $"Updated invoice {updated.InvoiceNumber} for {updated.SupplierReference}.", null, actor);
            return new ReturnActionResult(true, "The invoice has been updated.", []);
        }, cancellationToken);
    }

    public Task<ReturnActionResult> AddChargeScheduleItemAsync(
        ChargeScheduleEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == entry.ContractId);
            if (contract is null)
            {
                return new ReturnActionResult(false, "The selected contract no longer exists.", []);
            }

            var now = timeProvider.GetUtcNow();
            var item = new ChargeScheduleItem(
                Guid.NewGuid(),
                entry.ContractId,
                entry.ContractYear,
                entry.Description.Trim(),
                entry.ExpectedInvoiceDate,
                entry.ValueExVat,
                entry.IsOptionalExtension,
                now);
            database.ChargeScheduleItems.Add(item);
            var findings = ReportingRules.Validate(database);
            var errors = findings.Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == item.Id).ToList();
            if (errors.Count != 0)
            {
                database.ChargeScheduleItems.Remove(item);
                return new ReturnActionResult(false, "The charge schedule item was not added. Check its year, description and value.", errors);
            }

            RecordAudit(database, now, "ChargeScheduled", "ChargeSchedule", item.Id, $"Added contract-year {item.ContractYear} charge: {item.Description}.", null, actor);
            return new ReturnActionResult(true, "The charge schedule item has been added.", []);
        }, cancellationToken);

    public Task<ReturnActionResult> UpdateChargeScheduleItemAsync(
        Guid scheduleItemId,
        ChargeScheduleEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var existing = database.ChargeScheduleItems.SingleOrDefault(item => item.Id == scheduleItemId);
            if (existing is null || existing.ContractId != entry.ContractId)
            {
                return new ReturnActionResult(false, "The selected payment position no longer exists.", []);
            }

            var updated = existing with
            {
                ContractYear = entry.ContractYear,
                Description = entry.Description.Trim(),
                ExpectedInvoiceDate = entry.ExpectedInvoiceDate,
                ValueExVat = entry.ValueExVat,
                IsOptionalExtension = entry.IsOptionalExtension,
            };
            var index = database.ChargeScheduleItems.IndexOf(existing);
            database.ChargeScheduleItems[index] = updated;
            var errors = ReportingRules.Validate(database).Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == scheduleItemId).ToList();
            if (errors.Count != 0)
            {
                database.ChargeScheduleItems[index] = existing;
                return new ReturnActionResult(false, "The payment position was not updated. Check its year, description and value.", errors);
            }

            RecordAudit(database, timeProvider.GetUtcNow(), "ChargeScheduleUpdated", "ChargeSchedule", scheduleItemId, $"Updated contract-year {updated.ContractYear} charge: {updated.Description}.", null, actor);
            return new ReturnActionResult(true, "The payment position has been updated.", []);
        }, cancellationToken);

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

    public Task<ReportingCardModel> GetReportingCardAsync(
        FrameworkCode framework,
        string reportingMonth,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(reportingMonth);
        return store.ReadAsync(database => BuildReportingCard(database, framework, reportingMonth), cancellationToken);
    }

    public async Task<string> GetReportingCardTextAsync(
        FrameworkCode framework,
        string reportingMonth,
        CancellationToken cancellationToken = default)
    {
        var card = await GetReportingCardAsync(framework, reportingMonth, cancellationToken);
        var text = new StringBuilder();
        text.AppendLine($"{card.Framework.DisplayName} — {card.ReportingMonth} MI reporting information card");
        AppendCardSection(text, "Contracts", card.Contracts);
        AppendCardSection(text, "Invoices", card.Invoices);
        return text.ToString();
    }

    public Task<IReadOnlyList<TemplateConfigurationSummary>> GetTemplateConfigurationsAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => (IReadOnlyList<TemplateConfigurationSummary>)database.MiTemplates
            .OrderBy(item => item.Framework)
            .ThenByDescending(item => item.RegisteredAtUtc)
            .Select(ToTemplateSummary)
            .ToList(), cancellationToken);

    public async Task<TemplateRegistrationResult> RegisterTemplateAsync(
        FrameworkCode framework,
        string version,
        string workbookName,
        string guidanceUrl,
        string? notes,
        Stream workbook,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new TemplateRegistrationResult(false, "A template version is required.", null, []);
        }

        if (!Uri.TryCreate(guidanceUrl, UriKind.Absolute, out _))
        {
            return new TemplateRegistrationResult(false, "Provide the official guidance URL used to approve this template.", null, []);
        }

        await using var copiedWorkbook = new MemoryStream();
        await workbook.CopyToAsync(copiedWorkbook, cancellationToken);
        copiedWorkbook.Position = 0;
        var validation = await workbookExporter.ValidateTemplateAsync(framework, copiedWorkbook, cancellationToken);
        if (!validation.IsValid)
        {
            return new TemplateRegistrationResult(false, "The workbook does not match the expected MI template structure.", null, validation.Findings);
        }

        copiedWorkbook.Position = 0;
        var archived = await evidenceArchive.ArchiveAsync(
            new EvidenceArchiveRequest(
                workbookName,
                Path.Combine("templates", Frameworks.Get(framework).AgreementNumber, workbookName),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                copiedWorkbook),
            cancellationToken);

        return await store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            foreach (var existing in database.MiTemplates.Where(item => item.Framework == framework && item.IsActive))
            {
                database.MiTemplates[database.MiTemplates.FindIndex(item => item.Id == existing.Id)] = existing with { IsActive = false };
            }

            var evidence = new EvidenceRecord(
                Guid.NewGuid(),
                EvidenceKind.TemplateWorkbook,
                framework,
                null,
                workbookName,
                Path.Combine("templates", Frameworks.Get(framework).AgreementNumber, workbookName),
                archived.StoredRelativePath,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                archived.FileSizeBytes,
                archived.Sha256,
                null,
                now);
            database.Evidence.Add(evidence);
            var template = new MiTemplateConfiguration(
                Guid.NewGuid(),
                framework,
                version.Trim(),
                evidence.Id,
                workbookName,
                guidanceUrl.Trim(),
                NullIfWhiteSpace(notes),
                true,
                now);
            database.MiTemplates.Add(template);
            RecordAudit(database, now, "TemplateRegistered", "MiTemplate", template.Id, $"Registered {Frameworks.Get(framework).DisplayName} template {template.Version} ({workbookName}).", null, actor);
            return new TemplateRegistrationResult(true, "The approved template has been registered and is now active for this framework.", ToTemplateSummary(template), []);
        }, cancellationToken);
    }

    public async Task<ExportedReturn?> ExportReturnAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(reportingMonth);
        var exportContext = await store.ReadAsync(database =>
        {
            var template = database.MiTemplates.SingleOrDefault(item => item.Framework == framework && item.IsActive);
            var templateEvidence = template is null ? null : database.Evidence.SingleOrDefault(item => item.Id == template.EvidenceId);
            var contracts = database.Contracts.Where(item => item.Framework == framework && item.ReportMonth == reportingMonth).ToList();
            var invoices = database.Invoices.Where(item => item.Framework == framework && item.ReportMonth == reportingMonth).ToList();
            return new ExportContext(template, templateEvidence, contracts, invoices);
        }, cancellationToken);
        if (exportContext.Template is null || exportContext.TemplateEvidence is null)
        {
            return null;
        }

        await using var templateStream = await evidenceArchive.OpenReadAsync(exportContext.TemplateEvidence, cancellationToken);
        if (templateStream is null)
        {
            throw new InvalidOperationException("The registered template file is no longer available in the evidence archive.");
        }

        var generated = await workbookExporter.GenerateAsync(
            framework,
            templateStream,
            exportContext.Contracts,
            exportContext.Invoices,
            cancellationToken);
        await using var content = generated.Content;
        var fileName = $"{Frameworks.Get(framework).AgreementNumber}-MI-{reportingMonth}-{exportContext.Template.Version}.xlsx";
        var archived = await evidenceArchive.ArchiveAsync(
            new EvidenceArchiveRequest(
                fileName,
                Path.Combine("generated-returns", Frameworks.Get(framework).AgreementNumber, reportingMonth, fileName),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                content),
            cancellationToken);

        return await store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            var evidence = new EvidenceRecord(
                Guid.NewGuid(),
                EvidenceKind.GeneratedMiWorkbook,
                framework,
                reportingMonth,
                fileName,
                Path.Combine("generated-returns", Frameworks.Get(framework).AgreementNumber, reportingMonth, fileName),
                archived.StoredRelativePath,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                archived.FileSizeBytes,
                archived.Sha256,
                null,
                now);
            database.Evidence.Add(evidence);
            var monthlyReturn = EnsureReturn(database, framework, reportingMonth, fileName, now);
            var returnIndex = database.MonthlyReturns.FindIndex(item => item.Id == monthlyReturn.Id);
            database.MonthlyReturns[returnIndex] = monthlyReturn with { OriginalWorkbookName = fileName, UpdatedAtUtc = now };
            RecordAudit(database, now, "ReturnExported", "MonthlyReturn", monthlyReturn.Id, $"Generated {fileName} using template {exportContext.Template.Version}.", null, actor);
            return new ExportedReturn(evidence.Id, fileName, exportContext.Template.Version, generated.Findings);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AuditEventSummary>> GetAuditEventsAsync(int maximum = 100, CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => (IReadOnlyList<AuditEventSummary>)database.AuditEvents
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(Math.Clamp(maximum, 1, 500))
            .Select(item => new AuditEventSummary(item.Id, item.OccurredAtUtc, item.Action, item.EntityType, item.Summary, item.Reason, item.Actor))
            .ToList(), cancellationToken);

    public Task<IReadOnlyList<AuditEventSummary>> GetContractAuditEventsAsync(
        Guid contractId,
        int maximum = 100,
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (contract is null)
            {
                return (IReadOnlyList<AuditEventSummary>)[];
            }

            var relatedInvoiceIds = database.Invoices
                .Where(item => item.Framework == contract.Framework &&
                    ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(contract.SupplierReference))
                .Select(item => item.Id)
                .ToHashSet();
            return (IReadOnlyList<AuditEventSummary>)database.AuditEvents
                .Where(item =>
                    (item.EntityType == "Contract" && item.EntityId == contractId) ||
                    (item.EntityType == "Invoice" && item.EntityId is Guid invoiceId && relatedInvoiceIds.Contains(invoiceId)))
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(Math.Clamp(maximum, 1, 500))
                .Select(item => new AuditEventSummary(item.Id, item.OccurredAtUtc, item.Action, item.EntityType, item.Summary, item.Reason, item.Actor))
                .ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<AuditEventSummary>> GetInvoiceAuditEventsAsync(
        Guid invoiceId,
        int maximum = 100,
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var invoice = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            if (invoice is null)
            {
                return (IReadOnlyList<AuditEventSummary>)[];
            }

            var contract = database.Contracts.SingleOrDefault(item => item.Framework == invoice.Framework &&
                ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(invoice.SupplierReference));
            return (IReadOnlyList<AuditEventSummary>)database.AuditEvents
                .Where(item =>
                    (item.EntityType == "Invoice" && item.EntityId == invoiceId) ||
                    (contract is not null && item.EntityType == "Contract" && item.EntityId == contract.Id))
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(Math.Clamp(maximum, 1, 500))
                .Select(item => new AuditEventSummary(item.Id, item.OccurredAtUtc, item.Action, item.EntityType, item.Summary, item.Reason, item.Actor))
                .ToList();
        }, cancellationToken);

    public Task<ReturnActionResult> MarkSubmittedAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? submissionReference,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        UpdateReturnAsync(framework, reportingMonth, ReturnStatus.Submitted, submissionReference, null, actor, cancellationToken);

    public Task<ReturnActionResult> MarkNilReturnAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        UpdateReturnAsync(framework, reportingMonth, ReturnStatus.NilReturn, null, null, actor, cancellationToken);

    public Task<ReturnActionResult> RequestCorrectionAsync(
        FrameworkCode framework,
        string reportingMonth,
        string reason,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(new ReturnActionResult(false, "Record the reason for the correction request.", []));
        }

        return UpdateReturnAsync(framework, reportingMonth, ReturnStatus.CorrectionRequired, null, reason.Trim(), actor, cancellationToken);
    }

    private Task<ReturnActionResult> UpdateReturnAsync(
        FrameworkCode framework,
        string reportingMonth,
        ReturnStatus status,
        string? submissionReference,
        string? correctionReason,
        string? actor,
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

            var action = status switch
            {
                ReturnStatus.Submitted => "ReturnSubmitted",
                ReturnStatus.NilReturn => "NilReturnRecorded",
                ReturnStatus.CorrectionRequired => "CorrectionRequested",
                _ => "ReturnUpdated",
            };
            RecordAudit(database, now, action, "MonthlyReturn", existing.Id, $"{Frameworks.Get(framework).DisplayName} {reportingMonth} status changed to {status}.", correctionReason, actor);

            var message = status switch
            {
                ReturnStatus.NilReturn => "The nil return has been recorded.",
                ReturnStatus.CorrectionRequired => "The return has been marked for correction and the reason is retained in the audit trail.",
                _ => "The return has been recorded as submitted.",
            };
            return new ReturnActionResult(true, message, []);
        }, cancellationToken);
    }

    private static ReportingCardModel BuildReportingCard(
        RemiDatabase database,
        FrameworkCode framework,
        string reportingMonth)
    {
        var contracts = database.Contracts
            .Where(item => item.Framework == framework && item.ReportMonth == reportingMonth)
            .OrderBy(item => item.SupplierReference, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ReportingCardItem(item.SupplierReference, ContractCardFields(item)))
            .ToList();
        var invoices = database.Invoices
            .Where(item => item.Framework == framework && item.ReportMonth == reportingMonth)
            .OrderBy(item => item.InvoiceDate)
            .ThenBy(item => item.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ReportingCardItem($"{item.SupplierReference} · invoice {item.InvoiceNumber}", InvoiceCardFields(item)))
            .ToList();
        return new ReportingCardModel(Frameworks.Get(framework), reportingMonth, contracts, invoices);
    }

    private static IReadOnlyList<ReportingCardField> ContractCardFields(ContractRecord contract) =>
        contract.Framework == FrameworkCode.VerticalApplicationSolutions
            ?
            [
                CardField("Supplier Reference Number", contract.SupplierReference),
                CardField("Customer Organisation Name", contract.CustomerName),
                CardField("Customer Unique Reference Number (URN)", contract.CustomerUrn),
                CardField("Lot Number", contract.LotNumber),
                CardField("Product/Service Description", contract.ServiceDescription),
                CardField("Order Channel", contract.OrderChannel),
                CardField("Contract Start Date", contract.StartDate),
                CardField("Contract End Date", contract.EndDate),
                CardField("Total Contract Value", contract.TotalContractValueExVat),
            ]
            :
            [
                CardField("Supplier reference number", contract.SupplierReference),
                CardField("Customer Unique Reference Number (URN)", contract.CustomerUrn),
                CardField("Customer organisation name", contract.CustomerName),
                CardField("Contract start date", contract.StartDate),
                CardField("Contract end date", contract.EndDate),
                CardField("Lot number", contract.LotNumber),
                CardField("Service Group", contract.ServiceGroup),
                CardField("Digital Marketplace Service ID", contract.DigitalMarketplaceServiceId),
                CardField("Total contract value", contract.TotalContractValueExVat),
            ];

    private static IReadOnlyList<ReportingCardField> InvoiceCardFields(InvoiceRecord invoice) =>
        invoice.Framework == FrameworkCode.VerticalApplicationSolutions
            ?
            [
                CardField("Supplier Reference Number", invoice.SupplierReference),
                CardField("Customer Organisation Name", invoice.CustomerName),
                CardField("Customer Unique Reference Number (URN)", invoice.CustomerUrn),
                CardField("Customer Invoice/Credit Note Date", invoice.InvoiceDate),
                CardField("Customer Invoice/Credit Note Number", invoice.InvoiceNumber),
                CardField("Lot Number", invoice.LotNumber),
                CardField("Product/Service Group Level 1", invoice.ServiceGroup),
                CardField("Product/Service Group Level 2", invoice.ServiceGroupLevel2),
                CardField("Product/Service Description", invoice.ServiceDescription),
                CardField("Total Cost (ex VAT)", invoice.TotalCostExVat),
                CardField("Original Vendor", invoice.OriginalVendor),
                CardField("Subcontractor Name", invoice.SubcontractorName),
            ]
            :
            [
                CardField("Supplier reference number", invoice.SupplierReference),
                CardField("Customer Unique Reference Number (URN)", invoice.CustomerUrn),
                CardField("Customer organisation name", invoice.CustomerName),
                CardField("Customer invoice/credit note date", invoice.InvoiceDate),
                CardField("Customer invoice/credit note number", invoice.InvoiceNumber),
                CardField("Lot number", invoice.LotNumber),
                CardField("Service Group", invoice.ServiceGroup),
                CardField("Digital Marketplace Service ID", invoice.DigitalMarketplaceServiceId),
                CardField("Unit of Measure", invoice.UnitOfMeasure),
                CardField("Quantity", invoice.Quantity),
                CardField("Price per Unit", invoice.PricePerUnitExVat),
                CardField("Total Cost (ex VAT)", invoice.TotalCostExVat),
            ];

    private static ReportingCardField CardField(string label, object? value) => new(label, value switch
    {
        null => "Not recorded",
        DateOnly date => date.ToString("dd/MM/yyyy"),
        decimal number => number.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-GB")),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.GetCultureInfo("en-GB")) ?? "Not recorded",
    });

    private static void AppendCardSection(StringBuilder text, string title, IReadOnlyList<ReportingCardItem> items)
    {
        text.AppendLine();
        text.AppendLine(title);
        text.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            text.AppendLine("No reportable activity recorded.");
            return;
        }

        foreach (var item in items)
        {
            text.AppendLine();
            text.AppendLine(item.Title);
            foreach (var field in item.Fields)
            {
                text.AppendLine($"{field.Label,-52} {field.Value}");
            }
        }
    }

    private DashboardModel BuildDashboard(RemiDatabase database, DateOnly today, string? reportingMonth)
    {
        var defaultReportingMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1).ToString("yyyy-MM");
        var currentReportingMonth = IsValidReportingMonth(reportingMonth) ? reportingMonth! : defaultReportingMonth;
        var allFindings = ReportingRules.Validate(database);
        var findings = allFindings
            .Where(finding => Frameworks.All.Any(framework => IsInReportingPeriod(database, finding, framework.Code, currentReportingMonth)))
            .ToList();
        var summaries = Frameworks.All.Select(framework => new FrameworkSummary(
            framework,
            database.Contracts.Count(contract => contract.Framework == framework.Code),
            database.Invoices.Count(invoice => invoice.Framework == framework.Code),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.Submitted),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.Draft),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.NilReturn),
            database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework.Code && item.ReportMonth == currentReportingMonth)?.Status)).ToList();
        var readiness = Frameworks.All.Select(framework =>
        {
            var frameworkFindings = findings
                .Where(finding => IsInReportingPeriod(database, finding, framework.Code, currentReportingMonth))
                .ToList();
            return new FrameworkReadiness(
                framework,
                database.Contracts.Count(contract => contract.Framework == framework.Code && contract.ReportMonth == currentReportingMonth),
                database.Invoices.Count(invoice => invoice.Framework == framework.Code && invoice.ReportMonth == currentReportingMonth),
                database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework.Code && item.ReportMonth == currentReportingMonth)?.Status,
                frameworkFindings.Count(finding => finding.Severity == FindingSeverity.Error),
                frameworkFindings.Count(finding => finding.Severity == FindingSeverity.Warning));
        }).ToList();

        var progress = database.Contracts.Select(contract =>
        {
            var reportedInvoiceValue = database.Invoices
                .Where(invoice => invoice.Framework == contract.Framework &&
                    ReportingRules.NormaliseReference(invoice.SupplierReference) == ReportingRules.NormaliseReference(contract.SupplierReference))
                .Sum(invoice => invoice.TotalCostExVat);
            var reportedInvoiceCount = database.Invoices.Count(invoice =>
                invoice.Framework == contract.Framework &&
                ReportingRules.NormaliseReference(invoice.SupplierReference) == ReportingRules.NormaliseReference(contract.SupplierReference));
            var invoicePlanValue = database.InvoicePlanItems
                .Where(item => item.ContractId == contract.Id)
                .Sum(item => item.ExpectedValueExVat);
            var chargeSchedule = database.ChargeScheduleItems
                .Where(item => item.ContractId == contract.Id)
                .OrderBy(item => item.ContractYear)
                .ThenBy(item => item.ExpectedInvoiceDate)
                .ThenBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var chargeScheduleValue = chargeSchedule.Sum(item => item.ValueExVat);
            var plannedValue = chargeScheduleValue > 0 ? chargeScheduleValue : invoicePlanValue;
            var comparisonValue = plannedValue > 0 ? plannedValue : contract.TotalContractValueExVat;
            var evidence = EvidenceForContract(database, contract);
            return new ContractProgress(
                contract.Id,
                contract.Framework,
                contract.SupplierReference,
                contract.CustomerName,
                contract.CustomerUrn,
                contract.ReportMonth,
                contract.EndDate,
                contract.LotNumber,
                contract.ServiceGroup,
                contract.ServiceGroupLevel2,
                contract.ServiceDescription,
                contract.OrderChannel,
                contract.DigitalMarketplaceServiceId,
                contract.TotalContractValueExVat,
                reportedInvoiceCount,
                reportedInvoiceValue,
                comparisonValue,
                plannedValue > 0,
                comparisonValue == 0 ? 0 : reportedInvoiceValue / comparisonValue,
                chargeSchedule,
                evidence);
        })
        .OrderBy(item => item.CompletionRatio)
        .ThenBy(item => item.EndDate)
        .ToList();

        var attentionItems = findings
            .Select(finding => new AttentionItem(finding, AttentionRoute(database, finding)))
            .ToList();
        var recentActivity = database.AuditEvents
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(5)
            .Select(item => new AuditEventSummary(item.Id, item.OccurredAtUtc, item.Action, item.EntityType, item.Summary, item.Reason, item.Actor))
            .ToList();
        return new DashboardModel(summaries, progress, findings, attentionItems, currentReportingMonth, readiness, recentActivity);
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

    private static IReadOnlyList<EvidenceLink> EvidenceForContract(RemiDatabase database, ContractRecord contract) =>
        database.Evidence
            .Where(item => item.Framework == contract.Framework &&
                (string.Equals(
                    ReportingRules.NormaliseReference(item.ContractReference ?? string.Empty),
                    ReportingRules.NormaliseReference(contract.SupplierReference),
                    StringComparison.Ordinal) ||
                 (item.ReportMonth == contract.ReportMonth &&
                    string.Equals(item.FileName, contract.SourceWorkbook, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(item => item.OriginalRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(ToEvidenceLink)
            .ToList();

    private static IReadOnlyList<EvidenceLink> EvidenceForInvoice(RemiDatabase database, InvoiceRecord invoice) =>
        database.Evidence
            .Where(item => item.Framework == invoice.Framework &&
                (string.Equals(
                    ReportingRules.NormaliseReference(item.ContractReference ?? string.Empty),
                    ReportingRules.NormaliseReference(invoice.SupplierReference),
                    StringComparison.Ordinal) ||
                 (item.ReportMonth == invoice.ReportMonth &&
                    string.Equals(item.FileName, invoice.SourceWorkbook, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(item => item.OriginalRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(ToEvidenceLink)
            .ToList();

    private static PaymentScheduleUpdate AddPaymentScheduleItems(
        RemiDatabase database,
        ContractRecord contract,
        ContractPaymentSchedule schedule,
        DateTimeOffset now)
    {
        var added = 0;
        var relabelled = 0;
        var existing = database.ChargeScheduleItems
            .Where(item => item.ContractId == contract.Id)
            .ToList();
        var consumed = new HashSet<Guid>();

        foreach (var position in schedule.Positions.OrderBy(item => item.ContractYear).ThenBy(item => item.PositionInYear))
        {
            var description = PaymentPositionDescription(position);
            var matching = existing.FirstOrDefault(item =>
                !consumed.Contains(item.Id) &&
                item.ContractYear == position.ContractYear &&
                item.ValueExVat == position.ValueExVat &&
                string.Equals(item.Description, description, StringComparison.Ordinal));
            if (matching is not null)
            {
                if (matching.IsOptionalExtension != position.IsOptionalExtension)
                {
                    var databaseIndex = database.ChargeScheduleItems.IndexOf(matching);
                    var updatedItem = matching with { IsOptionalExtension = position.IsOptionalExtension };
                    database.ChargeScheduleItems[databaseIndex] = updatedItem;
                    existing[existing.IndexOf(matching)] = updatedItem;
                    relabelled++;
                }

                consumed.Add(matching.Id);
                continue;
            }

            var legacyLabel = existing.FirstOrDefault(item =>
                !consumed.Contains(item.Id) &&
                item.ContractYear == position.ContractYear &&
                item.ValueExVat == position.ValueExVat &&
                string.Equals(item.Description, "Annual licence and maintenance", StringComparison.Ordinal));
            if (legacyLabel is not null)
            {
                var databaseIndex = database.ChargeScheduleItems.IndexOf(legacyLabel);
                var relabelledItem = legacyLabel with { Description = description, IsOptionalExtension = position.IsOptionalExtension };
                database.ChargeScheduleItems[databaseIndex] = relabelledItem;
                existing[existing.IndexOf(legacyLabel)] = relabelledItem;
                consumed.Add(legacyLabel.Id);
                relabelled++;
                continue;
            }

            var created = new ChargeScheduleItem(
                Guid.NewGuid(),
                contract.Id,
                position.ContractYear,
                description,
                null,
                position.ValueExVat,
                position.IsOptionalExtension,
                now);
            database.ChargeScheduleItems.Add(created);
            existing.Add(created);
            consumed.Add(created.Id);
            added++;
        }

        return new PaymentScheduleUpdate(added, relabelled);
    }

    private static void AddManualPaymentScheduleItems(
        RemiDatabase database,
        ContractRecord contract,
        ContractPaymentPlanEntry paymentPlan,
        DateTimeOffset now)
    {
        foreach (var position in paymentPlan.Positions)
        {
            var term = position.ContractYear > paymentPlan.BaseTermYears ? "optional extension" : "base term";
            database.ChargeScheduleItems.Add(new ChargeScheduleItem(
                Guid.NewGuid(),
                contract.Id,
                position.ContractYear,
                $"Year {position.ContractYear} · {term} · {position.Description.Trim()}",
                null,
                position.ValueExVat,
                position.ContractYear > paymentPlan.BaseTermYears,
                now));
        }
    }

    private static string? ValidatePaymentPlan(ContractPaymentPlanEntry? paymentPlan)
    {
        if (paymentPlan is null)
        {
            return null;
        }

        if (paymentPlan.BaseTermYears < 1 || paymentPlan.OptionalExtensionYears < 0)
        {
            return "Enter at least one base-contract year and zero or more optional extension years.";
        }

        if (paymentPlan.Positions.Count == 0)
        {
            return "Add at least one payment position, or clear the payment-plan fields.";
        }

        var maximumYear = paymentPlan.BaseTermYears + paymentPlan.OptionalExtensionYears;
        if (paymentPlan.Positions.Any(position => position.ContractYear < 1 || position.ContractYear > maximumYear || string.IsNullOrWhiteSpace(position.Description) || position.ValueExVat <= 0))
        {
            return "Each payment position needs a year within the contract term, a description and a positive ex-VAT value.";
        }

        return null;
    }

    private static string PaymentPlanTerm(ContractPaymentPlanEntry paymentPlan) =>
        paymentPlan.OptionalExtensionYears == 0
            ? $"{paymentPlan.BaseTermYears}-year"
            : $"{paymentPlan.BaseTermYears}+{paymentPlan.OptionalExtensionYears}-year";

    private static string PaymentPlanSummary(ContractPaymentPlanEntry paymentPlan) =>
        $"{PaymentPlanTerm(paymentPlan)} term; {string.Join(" + ", paymentPlan.Positions.OrderBy(position => position.ContractYear).ThenBy(position => position.Description, StringComparer.OrdinalIgnoreCase).Select(position => $"Y{position.ContractYear} {position.Description}: {position.ValueExVat:0.00}"))}";

    private static string PaymentPositionDescription(ContractPaymentPosition position) =>
        position.ContractYear == 1 && position.PositionsInYear > 1
            ? position.PositionInYear switch
            {
                1 => "Annual licence and maintenance",
                2 => "Data Migration",
                3 => "Training",
                _ => "Other",
            }
            : "Annual licence and maintenance";

    private static ContractRecord MergeLedgerContractDetails(ContractRecord contract, LedgerContractScheduleEntry ledger) =>
        contract with
        {
            CustomerName = Coalesce(ledger.CustomerName, contract.CustomerName) ?? contract.CustomerName,
            CustomerUrn = Coalesce(ledger.CustomerUrn, contract.CustomerUrn),
            StartDate = ledger.StartDate ?? contract.StartDate,
            EndDate = ledger.EndDate ?? contract.EndDate,
            LotNumber = Coalesce(ledger.LotNumber, contract.LotNumber),
            ServiceGroup = Coalesce(ledger.ServiceGroup, contract.ServiceGroup),
            DigitalMarketplaceServiceId = Coalesce(ledger.DigitalMarketplaceServiceId, contract.DigitalMarketplaceServiceId),
            TotalContractValueExVat = ledger.TotalContractValueExVat is > 0 ? ledger.TotalContractValueExVat.Value : contract.TotalContractValueExVat,
        };

    private static string? Coalesce(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();

    private static string AttentionRoute(RemiDatabase database, ValidationFinding finding) => finding.EntityType switch
    {
        "Contract" when finding.EntityId is Guid id => $"/contracts/{id}",
        "Invoice" when finding.EntityId is Guid id => $"/invoices/{id}",
        "ChargeSchedule" when finding.EntityId is Guid id => database.ChargeScheduleItems
            .SingleOrDefault(item => item.Id == id) is { } schedule
                ? $"/contracts/{schedule.ContractId}"
                : "/contracts",
        _ => "/contracts",
    };

    private static TemplateConfigurationSummary ToTemplateSummary(MiTemplateConfiguration template) => new(
        template.Id,
        template.Framework,
        template.Version,
        template.WorkbookName,
        template.GuidanceUrl,
        template.Notes,
        template.IsActive,
        template.RegisteredAtUtc);

    private static void RecordAudit(
        RemiDatabase database,
        DateTimeOffset occurredAtUtc,
        string action,
        string entityType,
        Guid? entityId,
        string summary,
        string? reason,
        string? actor = null) =>
        database.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            occurredAtUtc,
            action,
            entityType,
            entityId,
            summary,
            NullIfWhiteSpace(reason),
            string.IsNullOrWhiteSpace(actor) ? "Local user" : actor.Trim()));

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    private static void ValidateReportingMonth(string reportingMonth)
    {
        if (!IsValidReportingMonth(reportingMonth))
        {
            throw new ArgumentException("Reporting month must use the yyyy-MM format.", nameof(reportingMonth));
        }
    }

    private static bool IsValidReportingMonth(string? reportingMonth) =>
        !string.IsNullOrWhiteSpace(reportingMonth)
        && DateOnly.TryParseExact($"{reportingMonth}-01", "yyyy-MM-dd", out _);

    private sealed record ExportContext(
        MiTemplateConfiguration? Template,
        EvidenceRecord? TemplateEvidence,
        IReadOnlyList<ContractRecord> Contracts,
        IReadOnlyList<InvoiceRecord> Invoices);

    private sealed record PaymentScheduleUpdate(int Added, int Relabelled);
}
