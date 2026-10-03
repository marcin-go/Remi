using System.Text;
using Remi.Domain;

namespace Remi.Application;

public sealed partial class ReportingWorkspace(
    IRemiStore store,
    IWorkbookImporter workbookImporter,
    IMiWorkbookExporter workbookExporter,
    IEvidenceArchive evidenceArchive,
    ICustomerUrnDirectory customerUrnDirectory,
    TimeProvider timeProvider)
{
    public Task<HomeDashboardModel> GetHomeDashboardAsync(string? reportingMonth, int endingWithinDays = 180,
        CancellationToken cancellationToken = default)
    {
        if (endingWithinDays is not (30 or 90 or 180)) throw new ArgumentOutOfRangeException(nameof(endingWithinDays));
        var today = Today();
        var month = IsValidReportingMonth(reportingMonth) ? reportingMonth! : new DateOnly(today.Year, today.Month, 1).AddMonths(-1).ToString("yyyy-MM");
        return store.ReadAsync(database => new HomeDashboardModel(
            OperationalHomeWorkspace.Build(database, today, endingWithinDays), month,
            BuildMonthlyReturnEntries(database, month, ReportingRules.Validate(database)),
            database.AuditEvents.OrderByDescending(item => item.OccurredAtUtc).Take(3)
                .Select(item => new AuditEventSummary(item.Id, item.OccurredAtUtc, item.Action, item.EntityType, item.Summary, item.Reason, item.Actor)).ToList()), cancellationToken);
    }

    public Task<DashboardModel> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        GetDashboardAsync(null, cancellationToken);

    public Task<DashboardModel> GetDashboardAsync(
        string? reportingMonth,
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => BuildDashboard(database, Today(), reportingMonth), cancellationToken);

    public Task<IReadOnlyList<string>> GetReportingPeriodsAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => (IReadOnlyList<string>)database.Contracts
            .Select(contract => contract.ReportMonth)
            .Concat(database.ContractChanges.Select(change => ReportingMonth(change.AgreementDate)))
            .Concat(database.Invoices.Select(invoice => invoice.ReportMonth))
            .Concat(database.MonthlyReturns.Select(monthlyReturn => monthlyReturn.ReportMonth))
            .Where(IsValidReportingMonth)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(month => month, StringComparer.Ordinal)
            .ToList(), cancellationToken);

    public Task<IReadOnlyList<FrameworkConfigurationSummary>> GetFrameworkConfigurationsAsync(
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => (IReadOnlyList<FrameworkConfigurationSummary>)Frameworks.All
            .Select(framework => new FrameworkConfigurationSummary(
                framework,
                FrameworkStartDate(database, framework),
                FrameworkEndDate(database, framework)))
            .OrderBy(item => item.StartDate is null)
            .ThenBy(item => item.StartDate)
            .ThenBy(item => item.Framework.DisplayName, StringComparer.Ordinal)
            .ToList(), cancellationToken);

    public Task<FrameworkConfigurationUpdateResult> UpdateFrameworkDatesAsync(
        FrameworkCode frameworkCode,
        DateOnly startDate,
        DateOnly endDate,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var definition = Frameworks.All.SingleOrDefault(item => item.Code == frameworkCode);
        if (definition is null)
        {
            return Task.FromResult(new FrameworkConfigurationUpdateResult(
                false,
                "The selected framework is not configured in Remi.",
                null));
        }

        if (endDate < startDate)
        {
            return Task.FromResult(new FrameworkConfigurationUpdateResult(
                false,
                "The framework end date cannot be earlier than its start date.",
                null));
        }

        return store.UpdateAsync(database =>
        {
            var existing = database.FrameworkConfigurations.SingleOrDefault(item => item.Framework == frameworkCode);
            var configuration = new FrameworkConfiguration(frameworkCode, startDate, endDate);
            if (existing is null)
            {
                database.FrameworkConfigurations.Add(configuration);
            }
            else
            {
                database.FrameworkConfigurations[database.FrameworkConfigurations.IndexOf(existing)] = configuration;
            }

            RecordAudit(
                database,
                timeProvider.GetUtcNow(),
                "FrameworkDatesUpdated",
                "FrameworkConfiguration",
                null,
                $"Set the operational dates for {definition.DisplayName} to {startDate:dd MMM yyyy} through {endDate:dd MMM yyyy}.",
                null,
                actor);
            return new FrameworkConfigurationUpdateResult(
                true,
                $"{definition.DisplayName} can accept contracts dated from {startDate:dd MMM yyyy} through {endDate:dd MMM yyyy}.",
                new FrameworkConfigurationSummary(definition, startDate, endDate));
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DigitalMarketplaceService>> GetDigitalMarketplaceServicesAsync(
        CancellationToken cancellationToken = default) =>
        GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud14, cancellationToken);

    public Task<IReadOnlyList<DigitalMarketplaceService>> GetDigitalMarketplaceServicesAsync(
        FrameworkCode framework,
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => (IReadOnlyList<DigitalMarketplaceService>)database.DigitalMarketplaceServices
            .Where(item => item.Framework == framework)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ServiceId, StringComparer.Ordinal)
            .ToList(), cancellationToken);

    public Task<DigitalMarketplaceServiceUpdateResult> UpdateDigitalMarketplaceServicesAsync(
        IEnumerable<DigitalMarketplaceService> services,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        UpdateDigitalMarketplaceServicesAsync(FrameworkCode.GCloud14, services, actor, cancellationToken);

    public Task<DigitalMarketplaceServiceUpdateResult> UpdateDigitalMarketplaceServicesAsync(
        FrameworkCode framework,
        IEnumerable<DigitalMarketplaceService> services,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!Frameworks.IsGCloud(framework))
        {
            return Task.FromResult(new DigitalMarketplaceServiceUpdateResult(false, "Digital Marketplace services can be configured only for G-Cloud frameworks.", []));
        }

        var updated = services
            .Select(item => new DigitalMarketplaceService(item.ServiceId.Trim(), item.Name.Trim(), framework))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ServiceId, StringComparer.Ordinal)
            .ToList();

        if (updated.Any(item => string.IsNullOrWhiteSpace(item.ServiceId) || string.IsNullOrWhiteSpace(item.Name)))
        {
            return Task.FromResult(new DigitalMarketplaceServiceUpdateResult(false, "Every Digital Marketplace service needs both a Service ID and product name.", updated));
        }

        if (updated.GroupBy(item => item.ServiceId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
        {
            return Task.FromResult(new DigitalMarketplaceServiceUpdateResult(false, "Each Digital Marketplace Service ID can be listed only once.", updated));
        }

        return store.UpdateAsync(database =>
        {
            database.DigitalMarketplaceServices.RemoveAll(item => item.Framework == framework);
            database.DigitalMarketplaceServices.AddRange(updated);
            var frameworkName = Frameworks.Get(framework).DisplayName;
            RecordAudit(
                database,
                timeProvider.GetUtcNow(),
                "DigitalMarketplaceServicesUpdated",
                "DigitalMarketplaceServiceConfiguration",
                null,
                $"Updated the {frameworkName} Digital Marketplace suggestion list with {updated.Count} service(s).",
                null,
                actor);
            return new DigitalMarketplaceServiceUpdateResult(true, $"Saved {updated.Count} {frameworkName} Digital Marketplace service suggestion(s).", updated);
        }, cancellationToken);
    }

    public Task<MonthlyReturnRegisterModel> GetMonthlyReturnRegisterAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database => BuildMonthlyReturnRegister(database, Today()), cancellationToken);

    public Task<CustomerUrnDirectoryStatus?> GetCustomerUrnDirectoryStatusAsync(
        CancellationToken cancellationToken = default) =>
        customerUrnDirectory.GetStatusAsync(cancellationToken);

    public Task<IReadOnlyList<CustomerUrnSuggestion>> GetCustomerUrnDirectoryEntriesAsync(
        CancellationToken cancellationToken = default) =>
        customerUrnDirectory.GetAllAsync(cancellationToken);

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
            var serviceParts = database.ContractServiceParts
                .Where(item => item.ContractId == contract.Id)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var changes = database.ContractChanges
                .Where(item => item.ContractId == contract.Id)
                .OrderBy(item => item.AgreementDate)
                .ThenBy(item => item.CreatedAtUtc)
                .ToList();
            var findings = ReportingRules.Validate(database)
                .Where(item => item.EntityId == contract.Id ||
                    (item.EntityType == "ChargeSchedule" && database.ChargeScheduleItems.Any(schedule => schedule.Id == item.EntityId && schedule.ContractId == contract.Id)) ||
                    (item.EntityType == "ContractChange" && changes.Any(change => change.Id == item.EntityId)))
                .ToList();
            var commercial = ContractPortfolioRules.CommercialPosition(contract, changes);
            var committedValue = ContractPortfolioRules.CommittedValue(contract, commercial, chargeSchedule,
                database.InvoicePlanItems.Where(item => item.ContractId == contractId).ToList());
            return new ContractDetailsModel(contract, invoices, chargeSchedule, serviceParts, changes,
                EvidenceForContract(database, contract), findings, committedValue);
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
            var changeId = database.InvoiceContractChangeLinks.SingleOrDefault(item => item.InvoiceId == invoice.Id)?.ContractChangeId;
            var change = changeId is Guid linkedChangeId
                ? database.ContractChanges.SingleOrDefault(item => item.Id == linkedChangeId)
                : null;
            var findings = ReportingRules.Validate(database)
                .Where(item => item.EntityId == invoice.Id)
                .ToList();
            return new InvoiceDetailsModel(invoice, contract, change, EvidenceForInvoice(database, invoice), findings);
        }, cancellationToken);

    public Task<DeletionImpact?> GetInvoiceDeletionImpactAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var invoice = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            if (invoice is null)
            {
                return null;
            }

            var objects = database.Evidence
                .Where(item => IsClipboardEvidenceFor(item, "invoice", invoiceId))
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new DeletionImpactItem("Supporting document", item.FileName))
                .ToList();
            var changeLink = database.InvoiceContractChangeLinks.SingleOrDefault(item => item.InvoiceId == invoiceId);
            if (changeLink is not null)
            {
                var change = database.ContractChanges.SingleOrDefault(item => item.Id == changeLink.ContractChangeId);
                objects.Add(new DeletionImpactItem(
                    "Contract change link",
                    change is null ? "Linked contract change" : $"{change.Kind} agreed {change.AgreementDate:dd MMM yyyy}"));
            }

            return new DeletionImpact(objects);
        }, cancellationToken);

    public Task<DeletionImpact?> GetContractDeletionImpactAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (contract is null)
            {
                return null;
            }

            var changes = database.ContractChanges
                .Where(item => item.ContractId == contractId)
                .OrderBy(item => item.AgreementDate)
                .ToList();
            var objects = new List<DeletionImpactItem>();
            objects.AddRange(changes.Select(item => new DeletionImpactItem("Contract change", $"{item.Kind} agreed {item.AgreementDate:dd MMM yyyy}")));
            objects.AddRange(database.ChargeScheduleItems
                .Where(item => item.ContractId == contractId)
                .OrderBy(item => item.ContractYear)
                .ThenBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
                .Select(item => new DeletionImpactItem("Payment schedule position", $"Year {item.ContractYear}: {item.Description}")));
            objects.AddRange(database.InvoicePlanItems
                .Where(item => item.ContractId == contractId)
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .Select(item => new DeletionImpactItem("Invoice plan item", item.Label)));
            objects.AddRange(database.ContractServiceParts
                .Where(item => item.ContractId == contractId)
                .OrderBy(item => item.SortOrder)
                .Select(item => new DeletionImpactItem("Operational part", item.Name)));

            var changeIds = changes.Select(item => item.Id).ToHashSet();
            objects.AddRange(database.Evidence
                .Where(item => IsClipboardEvidenceFor(item, "contract", contractId) ||
                    changeIds.Any(changeId => IsClipboardEvidenceFor(item, "contract-change", changeId)))
                .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new DeletionImpactItem("Supporting document", item.FileName)));
            objects.AddRange(database.ContractReportingOccurrences
                .Where(item => item.ContractId == contractId)
                .OrderBy(item => item.ReportingMonth, StringComparer.Ordinal)
                .Select(item => new DeletionImpactItem("Reporting occurrence", item.ReportingMonth)));

            return new DeletionImpact(objects);
        }, cancellationToken);

    public Task<IReadOnlyList<InvoiceRegisterItem>> GetInvoiceRegisterAsync(CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var findingsByInvoice = ReportingRules.Validate(database)
                .Where(finding => finding.EntityType == "Invoice" && finding.EntityId is not null)
                .GroupBy(finding => finding.EntityId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ValidationFinding>)group.ToList());

            return (IReadOnlyList<InvoiceRegisterItem>)database.Invoices
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
                    invoice.SourceWorkbook,
                    findingsByInvoice.GetValueOrDefault(invoice.Id, [])))
                .OrderByDescending(item => item.InvoiceDate)
                .ThenBy(item => item.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<InvoiceRegistrationContract>> GetInvoiceRegistrationContractsAsync(CancellationToken cancellationToken = default)
    {
        var today = Today();
        return store.ReadAsync(database =>
        {
            var changesByContract = database.ContractChanges.ToLookup(change => change.ContractId);
            var schedulesByContract = database.ChargeScheduleItems.ToLookup(item => item.ContractId);
            var legacyByContract = database.InvoicePlanItems.ToLookup(item => item.ContractId);
            var invoicesByContract = database.Invoices.ToLookup(invoice =>
                (invoice.Framework, ReportingRules.NormaliseReference(invoice.SupplierReference)));
            return (IReadOnlyList<InvoiceRegistrationContract>)database.Contracts
            .OrderBy(contract => contract.SupplierReference, StringComparer.OrdinalIgnoreCase)
            .Select(contract =>
            {
                var changes = changesByContract[contract.Id].OrderBy(change => change.AgreementDate).ToList();
                var commercial = ContractPortfolioRules.CommercialPosition(contract, changes);
                var invoicedValue = invoicesByContract[(contract.Framework, ReportingRules.NormaliseReference(contract.SupplierReference))]
                    .Sum(invoice => invoice.TotalCostExVat);
                var committedValue = ContractPortfolioRules.CommittedValue(contract, commercial,
                    schedulesByContract[contract.Id].ToList(), legacyByContract[contract.Id].ToList());
                var remainingValue = Math.Max(0, committedValue - invoicedValue);
                if (!ContractPortfolioRules.CanRegisterInvoice(
                    ContractPortfolioRules.Lifecycle(contract.StartDate, commercial.EndDate, today), remainingValue))
                    return null;
                return new InvoiceRegistrationContract(
                    contract.Id,
                    contract.Framework,
                    contract.SupplierReference,
                    contract.CustomerName,
                    contract.CustomerUrn,
                    contract.LotNumber,
                    contract.ServiceGroup,
                    contract.ServiceGroupLevel2,
                    contract.ServiceDescription,
                    contract.OrderChannel,
                    contract.DigitalMarketplaceServiceId,
                    changes,
                    remainingValue,
                    commercial.UnconfirmedChangeCount);
            })
            .OfType<InvoiceRegistrationContract>().ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Supplies the invoice defaults for a selected contract. Values from the most recently
    /// recorded invoice take precedence; otherwise contract values and standard MI defaults are
    /// used.
    /// </summary>
    public Task<InvoiceReportingSuggestion> GetInvoiceReportingSuggestionAsync(
        Guid contractId,
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (contract is null)
            {
                return new InvoiceReportingSuggestion(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    InvoiceReportingDefaults.UnitOfMeasure,
                    InvoiceReportingDefaults.Quantity,
                    InvoiceReportingDefaults.OriginalVendor,
                    InvoiceReportingDefaults.SubcontractorName);
            }

            var latestInvoice = database.Invoices
                .Where(invoice => invoice.Framework == contract.Framework &&
                    ReportingRules.NormaliseReference(invoice.SupplierReference) == ReportingRules.NormaliseReference(contract.SupplierReference))
                .OrderByDescending(invoice => invoice.InvoiceDate)
                .ThenByDescending(invoice => invoice.CreatedAtUtc)
                .FirstOrDefault();
            return new InvoiceReportingSuggestion(
                latestInvoice?.CustomerName ?? contract.CustomerName,
                latestInvoice?.CustomerUrn ?? contract.CustomerUrn ?? string.Empty,
                latestInvoice?.LotNumber ?? contract.LotNumber ?? string.Empty,
                latestInvoice?.ServiceGroup ?? contract.ServiceGroup ?? string.Empty,
                latestInvoice?.ServiceGroupLevel2 ?? contract.ServiceGroupLevel2 ?? string.Empty,
                latestInvoice?.ServiceDescription ?? contract.ServiceDescription ?? string.Empty,
                latestInvoice?.OrderChannel ?? contract.OrderChannel ?? string.Empty,
                latestInvoice?.DigitalMarketplaceServiceId ?? contract.DigitalMarketplaceServiceId ?? string.Empty,
                latestInvoice?.UnitOfMeasure ?? InvoiceReportingDefaults.UnitOfMeasure,
                latestInvoice?.Quantity is > 0 ? latestInvoice.Quantity.Value : InvoiceReportingDefaults.Quantity,
                latestInvoice?.OriginalVendor ?? InvoiceReportingDefaults.OriginalVendor,
                latestInvoice?.SubcontractorName ?? InvoiceReportingDefaults.SubcontractorName);
        }, cancellationToken);

    /// <summary>
    /// Completes historical records from the collection of supplied MI workbooks. A value from an
    /// invoice's own workbook always wins; another MI workbook for the same contract fills only a
    /// blank field. The retired Ledger is intentionally not used for this purpose.
    /// </summary>
    public Task<int> CompleteMigratedRecordsAsync(
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var completedCount = 0;
            var now = timeProvider.GetUtcNow();
            foreach (var invoice in database.Invoices.ToList())
            {
                var contract = database.Contracts.SingleOrDefault(item =>
                    item.Framework == invoice.Framework &&
                    ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(invoice.SupplierReference));
                var relatedInvoices = database.Invoices
                    .Where(item => item.Id != invoice.Id &&
                        item.Framework == invoice.Framework &&
                        ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(invoice.SupplierReference))
                    .OrderByDescending(item => item.InvoiceDate)
                    .ThenByDescending(item => item.CreatedAtUtc)
                    .ToList();
                var completed = CompleteMigratedInvoice(invoice, contract, relatedInvoices);
                if (completed == invoice)
                {
                    continue;
                }

                database.Invoices[database.Invoices.IndexOf(invoice)] = completed;
                completedCount++;
                RecordAudit(
                    database,
                    now,
                    "InvoiceCompletedFromMiWorkbooks",
                    "Invoice",
                    invoice.Id,
                    $"Completed blank MI fields for invoice {invoice.InvoiceNumber} for {invoice.SupplierReference} from the supplied MI workbooks.",
                    null,
                    actor);
            }

            return completedCount;
        }, cancellationToken);

    /// <summary>
    /// Imports a completed workbook from the one-off historical source-data baseline.
    /// This is deliberately not a monthly reporting workflow; new returns are generated from
    /// Remi's register and the approved template instead.
    /// </summary>
    public async Task<HistoricalWorkbookImportResult> ImportHistoricalWorkbookAsync(
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
                RecordAudit(database, now, "HistoricalWorkbookImported", "MonthlyReturn", null, $"Imported historical workbook {workbookName} for {Frameworks.Get(framework).DisplayName} {reportingMonth}.", null);
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
                    var existing = database.Contracts.Single(item =>
                        item.Framework == framework &&
                        ReportingRules.NormaliseReference(item.SupplierReference) == key.Item2);
                    var completed = MergeMiContractDetails(existing, contract);
                    if (completed != existing)
                    {
                        database.Contracts[database.Contracts.IndexOf(existing)] = completed;
                        RecordAudit(
                            database,
                            now,
                            "ContractCompletedFromMiWorkbooks",
                            "Contract",
                            existing.Id,
                            $"Completed blank MI fields for contract {existing.SupplierReference} from {workbookName}.",
                            null);
                    }
                    existingContracts++;
                    continue;
                }

                var importedContract = new ContractRecord(
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
                    now);
                database.Contracts.Add(importedContract);
                EnsureDefaultContractServicePart(database, importedContract, now);
                RecordAudit(database, now, "ContractImported", "Contract", importedContract.Id, $"Imported contract {contract.SupplierReference} from {workbookName}.", null);
                newContracts++;
            }

            var newInvoices = 0;
            var existingInvoiceCount = 0;
            foreach (var invoice in imported.Invoices)
            {
                var key = InvoiceKey(framework, invoice.SupplierReference, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.TotalCostExVat);
                if (!existingInvoices.Add(key))
                {
                    var existing = database.Invoices.Single(item =>
                        InvoiceKey(item.Framework, item.SupplierReference, item.InvoiceNumber, item.InvoiceDate, item.TotalCostExVat) == key);
                    var completed = MergeMiInvoiceDetails(existing, invoice);
                    if (completed != existing)
                    {
                        database.Invoices[database.Invoices.IndexOf(existing)] = completed;
                        RecordAudit(
                            database,
                            now,
                            "InvoiceCompletedFromMiWorkbooks",
                            "Invoice",
                            existing.Id,
                            $"Completed blank MI fields for invoice {existing.InvoiceNumber} for {existing.SupplierReference} from {workbookName}.",
                            null);
                    }
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
                    NullIfWhiteSpace(invoice.UnitOfMeasure),
                    invoice.Quantity,
                    invoice.PricePerUnitExVat,
                    invoice.TotalCostExVat,
                    NullIfWhiteSpace(invoice.OriginalVendor),
                    NullIfWhiteSpace(invoice.SubcontractorName),
                    reportingMonth,
                    imported.WorkbookName,
                    now));
                RecordAudit(database, now, "InvoiceImported", "Invoice", database.Invoices[^1].Id, $"Imported invoice {invoice.InvoiceNumber} for {invoice.SupplierReference} from {workbookName}.", null);
                newInvoices++;
            }

            var monthlyReturn = EnsureReturn(database, framework, reportingMonth, imported.WorkbookName, now);
            if (monthlyReturn.Status != ReturnStatus.Submitted)
            {
                var submittedReturn = monthlyReturn with
                {
                    Status = ReturnStatus.Submitted,
                    SubmittedAtUtc = monthlyReturn.SubmittedAtUtc,
                    SubmissionReference = monthlyReturn.SubmissionReference,
                    OriginalWorkbookName = imported.WorkbookName,
                    UpdatedAtUtc = now,
                };
                database.MonthlyReturns[database.MonthlyReturns.FindIndex(item => item.Id == monthlyReturn.Id)] = submittedReturn;
                RecordAudit(
                    database,
                    now,
                    "HistoricalReturnRecordedAsSubmitted",
                    "MonthlyReturn",
                    monthlyReturn.Id,
                    $"Recorded the supplied historical workbook {workbookName} as a submitted return for {Frameworks.Get(framework).DisplayName} {reportingMonth}.",
                    null);
            }
            return new HistoricalWorkbookImportResult(
                newContracts,
                existingContracts,
                newInvoices,
                existingInvoiceCount,
                evidenceArchived,
                ReportingRules.Validate(database));
        }, cancellationToken);
    }

    /// <summary>
    /// Records nil historical cycles that are absent from the supplied MI-workbook history.
    /// Existing non-draft return states are preserved because they carry an explicit user decision.
    /// </summary>
    public Task<int> EnsureHistoricalNilReturnsAsync(
        IEnumerable<HistoricalReturnPeriod> periods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var distinctPeriods = periods
            .Distinct()
            .ToList();
        foreach (var period in distinctPeriods)
        {
            _ = Frameworks.Get(period.Framework);
            ValidateReportingMonth(period.ReportingMonth);
        }

        return store.UpdateAsync(database =>
        {
            var now = timeProvider.GetUtcNow();
            var recorded = 0;
            foreach (var period in distinctPeriods)
            {
                var existing = database.MonthlyReturns.SingleOrDefault(item =>
                    item.Framework == period.Framework && item.ReportMonth == period.ReportingMonth);
                if (existing is { Status: not ReturnStatus.Draft })
                {
                    continue;
                }

                if (existing is null)
                {
                    var nilReturn = new MonthlyReturn(
                        Guid.NewGuid(),
                        period.Framework,
                        period.ReportingMonth,
                        ReturnStatus.NilReturn,
                        null,
                        null,
                        null,
                        now);
                    database.MonthlyReturns.Add(nilReturn);
                    RecordAudit(
                        database,
                        now,
                        "HistoricalNilReturnRecorded",
                        "MonthlyReturn",
                        nilReturn.Id,
                        $"Recorded {Frameworks.Get(period.Framework).DisplayName} {period.ReportingMonth} as a nil return because no historical MI workbook was supplied.",
                        null);
                }
                else
                {
                    database.MonthlyReturns[database.MonthlyReturns.FindIndex(item => item.Id == existing.Id)] = existing with
                    {
                        Status = ReturnStatus.NilReturn,
                        SubmittedAtUtc = null,
                        SubmissionReference = null,
                        OriginalWorkbookName = null,
                        UpdatedAtUtc = now,
                    };
                    RecordAudit(
                        database,
                        now,
                        "HistoricalDraftReturnRecordedAsNil",
                        "MonthlyReturn",
                        existing.Id,
                        $"Recorded {Frameworks.Get(period.Framework).DisplayName} {period.ReportingMonth} as a nil return because no historical MI workbook was supplied.",
                        null);
                }

                recorded++;
            }

            return recorded;
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
        var paymentPlanError = ValidatePaymentPlan(entry.PaymentPlan);
        if (paymentPlanError is not null)
        {
            return Task.FromResult(new ReturnActionResult(false, paymentPlanError, []));
        }

        return store.UpdateAsync(database =>
        {
            if (ValidateContractFrameworkWindow(database, entry) is { } frameworkDateError)
            {
                return new ReturnActionResult(false, frameworkDateError, []);
            }

            var now = timeProvider.GetUtcNow();
            var reportableContractValue = entry.PaymentPlan is { } registeredPlan
                ? registeredPlan.Positions.Where(position => !position.IsOptionalExtension).Sum(position => position.ValueExVat)
                : entry.TotalContractValueExVat;
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
                reportableContractValue,
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

            EnsureDefaultContractServicePart(database, record, now);
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
            return new ReturnActionResult(true, "The contract has been added to the reporting register.", [], record.Id);
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
                    EnsureDefaultContractServicePart(database, contract, now);
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
            var linkedChange = entry.ContractChangeId is Guid contractChangeId
                ? database.ContractChanges.SingleOrDefault(change => change.Id == contractChangeId)
                : null;
            if (entry.ContractChangeId is not null && (linkedChange is null || !InvoiceMatchesContract(entry, database.Contracts.SingleOrDefault(contract => contract.Id == linkedChange.ContractId))))
            {
                return new ReturnActionResult(false, "The selected contract change does not belong to this invoice's contract.", []);
            }
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
                NullIfWhiteSpace(entry.UnitOfMeasure) ?? InvoiceReportingDefaults.UnitOfMeasure,
                entry.Quantity ?? InvoiceReportingDefaults.Quantity,
                entry.PricePerUnitExVat ?? InvoiceReportingDefaults.PricePerUnitExVat(entry.TotalCostExVat),
                entry.TotalCostExVat,
                NullIfWhiteSpace(entry.OriginalVendor) ?? InvoiceReportingDefaults.OriginalVendor,
                NullIfWhiteSpace(entry.SubcontractorName) ?? InvoiceReportingDefaults.SubcontractorName,
                entry.ReportMonth,
                string.IsNullOrWhiteSpace(entry.SourceDescription) ? "Manual entry" : entry.SourceDescription.Trim(),
                now);
            database.Invoices.Add(record);
            if (linkedChange is not null)
            {
                database.InvoiceContractChangeLinks.Add(new InvoiceContractChangeLink(record.Id, linkedChange.Id));
            }
            var findings = ReportingRules.Validate(database);
            var errors = findings.Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == record.Id).ToList();
            var duplicate = database.Invoices.Count(invoice => InvoiceKey(invoice.Framework, invoice.SupplierReference, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.TotalCostExVat) == InvoiceKey(record.Framework, record.SupplierReference, record.InvoiceNumber, record.InvoiceDate, record.TotalCostExVat)) > 1;
            if (errors.Count != 0 || duplicate)
            {
                database.Invoices.Remove(record);
                database.InvoiceContractChangeLinks.RemoveAll(link => link.InvoiceId == record.Id);
                return new ReturnActionResult(false, "The invoice was not added. It must match a contract and not duplicate an existing invoice.", errors);
            }

            RecordAudit(database, now, "InvoiceRecorded", "Invoice", record.Id, $"Recorded invoice {record.InvoiceNumber} for {record.SupplierReference}.", null, actor);
            EnsureReturn(database, entry.Framework, entry.ReportMonth, null, now);
            return new ReturnActionResult(true, "The invoice has been recorded.", [], record.Id);
        }, cancellationToken);
    }

    public Task<ReturnActionResult> RecordContractChangeAsync(
        ContractChangeEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == entry.ContractId);
            if (contract is null)
            {
                return new ReturnActionResult(false, "The selected contract no longer exists.", []);
            }

            if (entry.AgreementDate is null)
            {
                return new ReturnActionResult(false, "Record the agreement date before reporting a contract change.", []);
            }

            if (entry.IncrementalValueExVat == 0)
            {
                return new ReturnActionResult(false, "Record the incremental ex-VAT value for the contract change.", []);
            }

            if (entry.Kind == ContractChangeKind.Extension && entry.IncrementalValueExVat < 0)
            {
                return new ReturnActionResult(false, "An extension must have a positive incremental value. Record a variation for a reduction.", []);
            }

            if (entry.EffectiveStartDate is not null && entry.EffectiveEndDate is not null && entry.EffectiveEndDate < entry.EffectiveStartDate)
            {
                return new ReturnActionResult(false, "The effective end date cannot be earlier than the effective start date.", []);
            }

            var duplicate = database.ContractChanges.Any(change =>
                change.ContractId == entry.ContractId &&
                change.Kind == entry.Kind &&
                change.AgreementDate == entry.AgreementDate.Value &&
                change.EffectiveStartDate == entry.EffectiveStartDate &&
                change.EffectiveEndDate == entry.EffectiveEndDate &&
                change.IncrementalValueExVat == entry.IncrementalValueExVat);
            if (duplicate)
            {
                return new ReturnActionResult(false, "An identical agreed contract change is already recorded for this contract.", []);
            }

            var now = timeProvider.GetUtcNow();
            var change = new ContractChangeRecord(
                Guid.NewGuid(),
                contract.Id,
                entry.Kind,
                entry.AgreementDate.Value,
                entry.EffectiveStartDate,
                entry.EffectiveEndDate,
                entry.IncrementalValueExVat,
                entry.WasProvidedForInOriginalCallOff,
                entry.IsConfirmed,
                NullIfWhiteSpace(entry.Reference),
                now);
            database.ContractChanges.Add(change);
            var findings = ReportingRules.Validate(database)
                .Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == change.Id)
                .ToList();
            if (findings.Count != 0)
            {
                database.ContractChanges.Remove(change);
                return new ReturnActionResult(false, "The contract change was not recorded. Resolve the highlighted fields first.", findings);
            }

            var reportingMonth = ReportingMonth(change.AgreementDate);
            EnsureReturn(database, contract.Framework, reportingMonth, null, now);
            RecordAudit(
                database,
                now,
                "ContractChangeRecorded",
                "ContractChange",
                change.Id,
                $"Recorded {change.Kind.ToString().ToLowerInvariant()} for {contract.SupplierReference}; it will report in {reportingMonth}.",
                change.IsConfirmed ? null : "The change is awaiting confirmation.",
                actor);
            return new ReturnActionResult(true, $"The {change.Kind.ToString().ToLowerInvariant()} has been recorded for {reportingMonth} reporting.", [], change.Id);
        }, cancellationToken);

    public Task<ReturnActionResult> ConfirmContractChangeAsync(
        Guid changeId,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var existing = database.ContractChanges.SingleOrDefault(change => change.Id == changeId);
            if (existing is null)
            {
                return new ReturnActionResult(false, "The selected contract change no longer exists.", []);
            }

            if (existing.IsConfirmed)
            {
                return new ReturnActionResult(true, "This contract change is already confirmed.", [], existing.Id);
            }

            var index = database.ContractChanges.IndexOf(existing);
            database.ContractChanges[index] = existing with { IsConfirmed = true };
            var contract = database.Contracts.Single(contract => contract.Id == existing.ContractId);
            RecordAudit(
                database,
                timeProvider.GetUtcNow(),
                "ContractChangeConfirmed",
                "ContractChange",
                existing.Id,
                $"Confirmed {existing.Kind.ToString().ToLowerInvariant()} for {contract.SupplierReference}.",
                null,
                actor);
            return new ReturnActionResult(true, "The contract change has been confirmed.", [], existing.Id);
        }, cancellationToken);

    public Task<ReturnActionResult> UpdateContractChangeAsync(
        Guid changeId,
        ContractChangeEntry entry,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var existing = database.ContractChanges.SingleOrDefault(change => change.Id == changeId);
            if (existing is null)
            {
                return new ReturnActionResult(false, "The selected contract change no longer exists.", []);
            }

            if (existing.ContractId != entry.ContractId)
            {
                return new ReturnActionResult(false, "A contract change cannot be moved to a different contract.", []);
            }
            var linkedInvoiceIds = database.InvoiceContractChangeLinks.Where(item => item.ContractChangeId == changeId).Select(item => item.InvoiceId).ToHashSet();
            if (entry.Kind != existing.Kind && database.InvoicePaymentAllocations.Any(item => linkedInvoiceIds.Contains(item.InvoiceId)))
                return new ReturnActionResult(false, "Remove the linked invoice payment allocations before changing this agreement's kind.", []);

            var contract = database.Contracts.SingleOrDefault(item => item.Id == existing.ContractId);
            if (contract is null)
            {
                return new ReturnActionResult(false, "The contract for this change no longer exists.", []);
            }

            if (entry.AgreementDate is null)
            {
                return new ReturnActionResult(false, "Record the agreement date before reporting a contract change.", []);
            }

            if (entry.IncrementalValueExVat == 0)
            {
                return new ReturnActionResult(false, "Record the incremental ex-VAT value for the contract change.", []);
            }

            if (entry.Kind == ContractChangeKind.Extension && entry.IncrementalValueExVat < 0)
            {
                return new ReturnActionResult(false, "An extension must have a positive incremental value. Record a variation for a reduction.", []);
            }

            if (entry.EffectiveStartDate is not null && entry.EffectiveEndDate is not null && entry.EffectiveEndDate < entry.EffectiveStartDate)
            {
                return new ReturnActionResult(false, "The effective end date cannot be earlier than the effective start date.", []);
            }

            var duplicate = database.ContractChanges.Any(change =>
                change.Id != changeId &&
                change.ContractId == entry.ContractId &&
                change.Kind == entry.Kind &&
                change.AgreementDate == entry.AgreementDate.Value &&
                change.EffectiveStartDate == entry.EffectiveStartDate &&
                change.EffectiveEndDate == entry.EffectiveEndDate &&
                change.IncrementalValueExVat == entry.IncrementalValueExVat);
            if (duplicate)
            {
                return new ReturnActionResult(false, "An identical agreed contract change is already recorded for this contract.", []);
            }

            var updated = existing with
            {
                Kind = entry.Kind,
                AgreementDate = entry.AgreementDate.Value,
                EffectiveStartDate = entry.EffectiveStartDate,
                EffectiveEndDate = entry.EffectiveEndDate,
                IncrementalValueExVat = entry.IncrementalValueExVat,
                WasProvidedForInOriginalCallOff = entry.WasProvidedForInOriginalCallOff,
                IsConfirmed = existing.IsConfirmed || entry.IsConfirmed,
                Reference = NullIfWhiteSpace(entry.Reference),
            };
            var index = database.ContractChanges.IndexOf(existing);
            database.ContractChanges[index] = updated;
            var findings = ReportingRules.Validate(database)
                .Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == updated.Id)
                .ToList();
            if (findings.Count != 0)
            {
                database.ContractChanges[index] = existing;
                return new ReturnActionResult(false, "The contract change was not updated. Resolve the highlighted fields first.", findings);
            }

            var now = timeProvider.GetUtcNow();
            var reportingMonth = ReportingMonth(updated.AgreementDate);
            EnsureReturn(database, contract.Framework, reportingMonth, null, now);
            RecordAudit(
                database,
                now,
                "ContractChangeUpdated",
                "ContractChange",
                updated.Id,
                $"Corrected {updated.Kind.ToString().ToLowerInvariant()} for {contract.SupplierReference}; it will report in {reportingMonth}.",
                null,
                actor);
            return new ReturnActionResult(true, "The contract change has been corrected.", [], updated.Id);
        }, cancellationToken);

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

            var reconciledPositionIds = database.ChargeScheduleItems.Where(item => item.ContractId == contractId).Select(item => item.Id)
                .Concat(database.InvoicePlanItems.Where(item => item.ContractId == contractId).Select(item => item.Id)).ToHashSet();
            if ((entry.Framework != existing.Framework || ReportingRules.NormaliseReference(entry.SupplierReference) != ReportingRules.NormaliseReference(existing.SupplierReference))
                && database.InvoicePaymentAllocations.Any(item => reconciledPositionIds.Contains(item.PositionId)))
                return new ReturnActionResult(false, "Remove the payment allocations before changing this contract's framework or supplier reference.", []);

            if (ValidateContractFrameworkWindow(database, entry) is { } frameworkDateError)
            {
                return new ReturnActionResult(false, frameworkDateError, []);
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

            var assignedContract = database.Contracts.SingleOrDefault(contract => contract.Framework == existing.Framework &&
                ReportingRules.NormaliseReference(contract.SupplierReference) == ReportingRules.NormaliseReference(existing.SupplierReference));
            entry = entry with { Framework = assignedContract?.Framework ?? existing.Framework };

            var paymentAllocations = database.InvoicePaymentAllocations.Where(item => item.InvoiceId == invoiceId).ToList();
            var existingAgreementId = database.InvoiceContractChangeLinks.SingleOrDefault(item => item.InvoiceId == invoiceId)?.ContractChangeId;
            if (entry.ContractChangeId != existingAgreementId && paymentAllocations.Any(allocation => allocation.Source == BillingPositionSource.ChargeSchedule
                && database.ChargeScheduleItems.Any(item => item.Id == allocation.PositionId && item.IsOptionalExtension)))
                return new ReturnActionResult(false, "Remove the optional-position allocations before changing this invoice's extension agreement.", []);
            if (paymentAllocations.Count > 0 &&
                (ReportingRules.NormaliseReference(entry.SupplierReference) != ReportingRules.NormaliseReference(existing.SupplierReference)
                 || Math.Sign(entry.TotalCostExVat) != Math.Sign(existing.TotalCostExVat)
                 || Math.Abs(entry.TotalCostExVat) < Math.Abs(paymentAllocations.Sum(item => item.ValueExVat))))
                return new ReturnActionResult(false, "Remove or adjust this invoice's payment allocations before changing its contract or reducing its value below the allocated amount.", []);

            var linkedChange = entry.ContractChangeId is Guid contractChangeId
                ? database.ContractChanges.SingleOrDefault(change => change.Id == contractChangeId)
                : null;
            if (entry.ContractChangeId is not null && (linkedChange is null || !InvoiceMatchesContract(entry, database.Contracts.SingleOrDefault(contract => contract.Id == linkedChange.ContractId))))
            {
                return new ReturnActionResult(false, "The selected contract change does not belong to this invoice's contract.", []);
            }

            var updated = new InvoiceRecord(existing.Id, entry.Framework, entry.SupplierReference.Trim(), entry.CustomerName.Trim(), NullIfWhiteSpace(entry.CustomerUrn), entry.InvoiceDate, entry.InvoiceNumber.Trim(), NullIfWhiteSpace(entry.LotNumber), NullIfWhiteSpace(entry.ServiceGroup), NullIfWhiteSpace(entry.ServiceGroupLevel2), NullIfWhiteSpace(entry.ServiceDescription), NullIfWhiteSpace(entry.OrderChannel), NullIfWhiteSpace(entry.DigitalMarketplaceServiceId), NullIfWhiteSpace(entry.UnitOfMeasure) ?? InvoiceReportingDefaults.UnitOfMeasure, entry.Quantity ?? InvoiceReportingDefaults.Quantity, entry.PricePerUnitExVat ?? InvoiceReportingDefaults.PricePerUnitExVat(entry.TotalCostExVat), entry.TotalCostExVat, NullIfWhiteSpace(entry.OriginalVendor) ?? InvoiceReportingDefaults.OriginalVendor, NullIfWhiteSpace(entry.SubcontractorName) ?? InvoiceReportingDefaults.SubcontractorName, entry.ReportMonth, existing.SourceWorkbook, existing.CreatedAtUtc);
            var index = database.Invoices.IndexOf(existing);
            var previousLinks = database.InvoiceContractChangeLinks.Where(link => link.InvoiceId == invoiceId).ToList();
            database.Invoices[index] = updated;
            database.InvoiceContractChangeLinks.RemoveAll(link => link.InvoiceId == invoiceId);
            if (linkedChange is not null)
            {
                database.InvoiceContractChangeLinks.Add(new InvoiceContractChangeLink(invoiceId, linkedChange.Id));
            }
            var findings = ReportingRules.Validate(database);
            var duplicate = database.Invoices.Any(invoice => invoice.Id != invoiceId && InvoiceKey(invoice.Framework, invoice.SupplierReference, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.TotalCostExVat) == InvoiceKey(updated.Framework, updated.SupplierReference, updated.InvoiceNumber, updated.InvoiceDate, updated.TotalCostExVat));
            var errors = findings.Where(finding => finding.Severity == FindingSeverity.Error && finding.EntityId == invoiceId).ToList();
            if (errors.Count != 0 || duplicate)
            {
                database.Invoices[index] = existing;
                database.InvoiceContractChangeLinks.RemoveAll(link => link.InvoiceId == invoiceId);
                database.InvoiceContractChangeLinks.AddRange(previousLinks);
                return new ReturnActionResult(false, "The invoice was not updated. It must match a contract and not duplicate an existing invoice.", errors);
            }

            RecordAudit(database, timeProvider.GetUtcNow(), "InvoiceUpdated", "Invoice", invoiceId, $"Updated invoice {updated.InvoiceNumber} for {updated.SupplierReference}.", null, actor);
            return new ReturnActionResult(true, "The invoice has been updated.", []);
        }, cancellationToken);
    }

    public async Task<ReturnActionResult> DeleteInvoiceAsync(
        Guid invoiceId,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var deletion = await store.UpdateAsync(database =>
        {
            var existing = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            if (existing is null)
            {
                return new InvoiceDeletionResult(
                    new ReturnActionResult(false, "The selected invoice no longer exists.", []),
                    []);
            }

            if (database.InvoicePaymentAllocations.Any(item => item.InvoiceId == invoiceId))
                return new InvoiceDeletionResult(new ReturnActionResult(false, "Remove this invoice's payment allocations before deleting it.", []), []);

            var invoiceOnlyEvidence = database.Evidence
                .Where(item => IsClipboardEvidenceFor(item, "invoice", invoiceId))
                .ToList();
            database.Invoices.Remove(existing);
            database.InvoiceContractChangeLinks.RemoveAll(link => link.InvoiceId == invoiceId);
            database.Evidence.RemoveAll(item => invoiceOnlyEvidence.Contains(item));
            RecordAudit(
                database,
                timeProvider.GetUtcNow(),
                "InvoiceDeleted",
                "Invoice",
                invoiceId,
                $"Deleted invoice {existing.InvoiceNumber} for {existing.SupplierReference}.",
                null,
                actor);

            var unreferencedEvidence = invoiceOnlyEvidence
                .Where(item => !database.Evidence.Any(remaining => string.Equals(
                    remaining.StoredRelativePath,
                    item.StoredRelativePath,
                    StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return new InvoiceDeletionResult(
                new ReturnActionResult(true, "The invoice has been deleted.", [], invoiceId),
                unreferencedEvidence);
        }, cancellationToken);

        if (!deletion.Result.Succeeded)
        {
            return deletion.Result;
        }

        var evidenceCleanupSucceeded = true;
        foreach (var evidence in deletion.UnreferencedEvidence)
        {
            try
            {
                await evidenceArchive.DeleteAsync(evidence, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                evidenceCleanupSucceeded = false;
            }
        }

        return evidenceCleanupSucceeded
            ? deletion.Result
            : deletion.Result with { Message = "The invoice was deleted, but one or more supporting files could not be removed from the archive." };
    }

    public async Task<ReturnActionResult> DeleteContractAsync(
        Guid contractId,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var deletion = await store.UpdateAsync(database =>
        {
            var existing = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (existing is null)
            {
                return new ContractDeletionResult(
                    new ReturnActionResult(false, "The selected contract no longer exists.", []),
                    []);
            }

            var changes = database.ContractChanges.Where(item => item.ContractId == contractId).ToList();
            var allocatedPositions = database.ChargeScheduleItems.Where(item => item.ContractId == contractId).Select(item => item.Id)
                .Concat(database.InvoicePlanItems.Where(item => item.ContractId == contractId).Select(item => item.Id)).ToHashSet();
            if (database.InvoicePaymentAllocations.Any(item => allocatedPositions.Contains(item.PositionId)))
                return new ContractDeletionResult(new ReturnActionResult(false, "Remove this contract's payment allocations before deleting it.", []), []);
            var changeIds = changes.Select(item => item.Id).ToHashSet();
            var privateEvidence = database.Evidence
                .Where(item => IsClipboardEvidenceFor(item, "contract", contractId) ||
                    changeIds.Any(changeId => IsClipboardEvidenceFor(item, "contract-change", changeId)))
                .ToList();

            database.Contracts.Remove(existing);
            database.ContractChanges.RemoveAll(item => changeIds.Contains(item.Id));
            database.InvoiceContractChangeLinks.RemoveAll(item => changeIds.Contains(item.ContractChangeId));
            database.InvoicePlanItems.RemoveAll(item => item.ContractId == contractId);
            database.ChargeScheduleItems.RemoveAll(item => item.ContractId == contractId);
            database.ContractServiceParts.RemoveAll(item => item.ContractId == contractId);
            database.ContractReportingOccurrences.RemoveAll(item => item.ContractId == contractId);
            database.Evidence.RemoveAll(item => privateEvidence.Contains(item));
            RecordAudit(
                database,
                timeProvider.GetUtcNow(),
                "ContractDeleted",
                "Contract",
                contractId,
                $"Deleted contract {existing.SupplierReference} for {existing.CustomerName}.",
                null,
                actor);

            var unreferencedEvidence = privateEvidence
                .Where(item => !database.Evidence.Any(remaining => string.Equals(
                    remaining.StoredRelativePath,
                    item.StoredRelativePath,
                    StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return new ContractDeletionResult(
                new ReturnActionResult(true, "The contract and its related objects have been deleted.", [], contractId),
                unreferencedEvidence);
        }, cancellationToken);

        if (!deletion.Result.Succeeded)
        {
            return deletion.Result;
        }

        var evidenceCleanupSucceeded = true;
        foreach (var evidence in deletion.UnreferencedEvidence)
        {
            try
            {
                await evidenceArchive.DeleteAsync(evidence, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                evidenceCleanupSucceeded = false;
            }
        }

        return evidenceCleanupSucceeded
            ? deletion.Result
            : deletion.Result with { Message = "The contract was deleted, but one or more supporting files could not be removed from the archive." };
    }

    public async Task<ReturnActionResult> DeleteContractEvidenceAsync(
        Guid contractId,
        Guid evidenceId,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var deletion = await store.UpdateAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (contract is null)
            {
                return new ContractEvidenceDeletionResult(
                    new ReturnActionResult(false, "The selected contract no longer exists.", []),
                    null);
            }

            var evidence = database.Evidence.SingleOrDefault(item => item.Id == evidenceId);
            var changeIds = database.ContractChanges
                .Where(change => change.ContractId == contractId)
                .Select(change => change.Id)
                .ToHashSet();
            if (evidence is null || !IsDeletableContractEvidence(evidence, contractId, changeIds))
            {
                return new ContractEvidenceDeletionResult(
                    new ReturnActionResult(false, "Only supporting documents added directly to this contract can be deleted.", []),
                    null);
            }

            database.Evidence.Remove(evidence);
            RecordAudit(
                database,
                timeProvider.GetUtcNow(),
                "ContractEvidenceDeleted",
                "Contract",
                contractId,
                $"Deleted supporting document {evidence.FileName} from contract {contract.SupplierReference}.",
                null,
                actor);

            var unreferencedEvidence = database.Evidence.Any(remaining => string.Equals(
                remaining.StoredRelativePath,
                evidence.StoredRelativePath,
                StringComparison.OrdinalIgnoreCase))
                ? null
                : evidence;
            return new ContractEvidenceDeletionResult(
                new ReturnActionResult(true, "The supporting document has been deleted.", [], contractId),
                unreferencedEvidence);
        }, cancellationToken);

        if (!deletion.Result.Succeeded || deletion.UnreferencedEvidence is null)
        {
            return deletion.Result;
        }

        try
        {
            await evidenceArchive.DeleteAsync(deletion.UnreferencedEvidence, cancellationToken);
            return deletion.Result;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return deletion.Result with { Message = "The document was removed from the contract, but its file could not be removed from the archive." };
        }
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

            if (entry.ContractServicePartId is Guid partId && !database.ContractServiceParts.Any(part => part.Id == partId && part.ContractId == entry.ContractId))
            {
                return new ReturnActionResult(false, "The selected contract part no longer exists.", []);
            }

            var legacyIds = database.InvoicePlanItems.Where(item => item.ContractId == entry.ContractId).Select(item => item.Id).ToHashSet();
            if (!database.ChargeScheduleItems.Any(item => item.ContractId == entry.ContractId)
                && database.InvoicePaymentAllocations.Any(item => item.Source == BillingPositionSource.LegacyPlan && legacyIds.Contains(item.PositionId)))
                return new ReturnActionResult(false, "Remove the legacy payment allocations before replacing the legacy plan with a charge schedule.", []);

            var now = timeProvider.GetUtcNow();
            var item = new ChargeScheduleItem(
                Guid.NewGuid(),
                entry.ContractId,
                entry.ContractServicePartId,
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
            if (entry.ContractServicePartId is Guid partId && !database.ContractServiceParts.Any(part => part.Id == partId && part.ContractId == entry.ContractId))
            {
                return new ReturnActionResult(false, "The selected contract part no longer exists.", []);
            }

            var updated = existing with
            {
                ContractYear = entry.ContractYear,
                Description = entry.Description.Trim(),
                ExpectedInvoiceDate = entry.ExpectedInvoiceDate,
                ValueExVat = entry.ValueExVat,
                IsOptionalExtension = entry.IsOptionalExtension,
                ContractServicePartId = entry.ContractServicePartId,
            };
            var reconciledValue = database.InvoicePaymentAllocations.Where(item => item.PositionId == scheduleItemId && item.Source == BillingPositionSource.ChargeSchedule).Sum(item => item.ValueExVat);
            if (database.InvoicePaymentAllocations.Any(item => item.PositionId == scheduleItemId && item.Source == BillingPositionSource.ChargeSchedule)
                && (entry.IsOptionalExtension && !existing.IsOptionalExtension || entry.ValueExVat < reconciledValue || entry.ValueExVat <= 0))
                return new ReturnActionResult(false, "Remove or adjust payment allocations before making this position optional or reducing its value below the reconciled amount.", []);
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

    public Task<ReturnActionResult> DeleteChargeScheduleItemAsync(
        Guid scheduleItemId,
        Guid contractId,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var existing = database.ChargeScheduleItems.SingleOrDefault(item => item.Id == scheduleItemId);
            if (existing is null || existing.ContractId != contractId)
            {
                return new ReturnActionResult(false, "The selected payment position no longer exists.", []);
            }

            if (database.InvoicePaymentAllocations.Any(item => item.PositionId == scheduleItemId && item.Source == BillingPositionSource.ChargeSchedule))
                return new ReturnActionResult(false, "Remove the invoice allocations before deleting this payment position.", []);
            database.ChargeScheduleItems.Remove(existing);
            RecordAudit(database, timeProvider.GetUtcNow(), "ChargeScheduleDeleted", "ChargeSchedule", scheduleItemId, $"Removed contract-year {existing.ContractYear} charge: {existing.Description}.", null, actor);
            return new ReturnActionResult(true, "The payment position has been removed.", []);
        }, cancellationToken);

    public Task<ContractOperationsUpdateResult> UpdateContractOperationsAsync(
        Guid contractId,
        IReadOnlyList<ContractServicePartEntry> entries,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
            if (contract is null)
            {
                return new ContractOperationsUpdateResult(false, "The selected contract no longer exists.", [], []);
            }

            if (entries.Count is < 1 or > 20)
            {
                return new ContractOperationsUpdateResult(false, "Keep between one and twenty operational parts for a contract.", [], []);
            }

            var normalisedEntries = entries
                .OrderBy(entry => entry.SortOrder)
                .Select((entry, index) => entry with
                {
                    Name = entry.Name.Trim(),
                    SortOrder = index,
                })
                .ToList();
            if (normalisedEntries.Any(entry => string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 120))
            {
                return new ContractOperationsUpdateResult(false, "Each operational part needs a name of no more than 120 characters.", [], []);
            }
            if (normalisedEntries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalisedEntries.Count)
            {
                return new ContractOperationsUpdateResult(false, "Operational part names must be unique within the contract.", [], []);
            }
            if (normalisedEntries.Where(entry => entry.Id is not null).Select(entry => entry.Id).Distinct().Count() != normalisedEntries.Count(entry => entry.Id is not null))
            {
                return new ContractOperationsUpdateResult(false, "The same operational part cannot be included twice.", [], []);
            }

            var existing = database.ContractServiceParts
                .Where(part => part.ContractId == contractId)
                .OrderBy(part => part.SortOrder)
                .ToList();
            var existingById = existing.ToDictionary(part => part.Id);
            if (normalisedEntries.Any(entry => entry.Id is Guid id && !existingById.ContainsKey(id)))
            {
                return new ContractOperationsUpdateResult(false, "One of the operational parts no longer belongs to this contract.", existing, []);
            }

            var retainedIds = normalisedEntries
                .Where(entry => entry.Id is not null)
                .Select(entry => entry.Id!.Value)
                .ToHashSet();
            var removed = existing.Where(part => !retainedIds.Contains(part.Id)).ToList();
            if (removed.Any(part => database.ChargeScheduleItems.Any(item => item.ContractServicePartId == part.Id)))
            {
                return new ContractOperationsUpdateResult(false, "A part linked to a charge cannot be removed. Reassign that charge first.", existing, []);
            }

            var now = timeProvider.GetUtcNow();
            var updated = normalisedEntries.Select(entry =>
            {
                if (entry.Id is Guid id)
                {
                    var original = existingById[id];
                    return original with
                    {
                        Name = entry.Name,
                        GoLiveDate = entry.GoLiveDate,
                        SortOrder = entry.SortOrder,
                    };
                }

                return new ContractServicePart(
                    Guid.NewGuid(),
                    contractId,
                    entry.Name,
                    entry.GoLiveDate,
                    entry.SortOrder,
                    now);
            }).ToList();
            var newlyLiveIds = updated
                .Where(part => part.GoLiveDate is not null &&
                    (!existingById.TryGetValue(part.Id, out var prior) || prior.GoLiveDate is null))
                .Select(part => part.Id)
                .ToList();

            database.ContractServiceParts.RemoveAll(part => part.ContractId == contractId);
            database.ContractServiceParts.AddRange(updated);
            var liveCount = updated.Count(part => part.GoLiveDate is not null);
            var summary = liveCount switch
            {
                0 => $"Recorded {updated.Count} operational part(s); none is live yet.",
                _ when liveCount == updated.Count => $"Recorded {updated.Count} operational part(s); all are live.",
                _ => $"Recorded {updated.Count} operational part(s); {liveCount} are live.",
            };
            RecordAudit(database, now, "ContractOperationsUpdated", "Contract", contractId, summary, null, actor);
            foreach (var partId in newlyLiveIds)
            {
                var part = updated.Single(item => item.Id == partId);
                RecordAudit(database, now, "ContractPartWentLive", "Contract", contractId, $"{part.Name} went live on {part.GoLiveDate:dd MMM yyyy}.", null, actor);
            }

            return new ContractOperationsUpdateResult(true, "The operational delivery record has been updated.", updated, newlyLiveIds);
        }, cancellationToken);

    public async Task<IReadOnlyList<ReportingEvidence>> GetReportingEvidenceAsync(
        FrameworkCode framework,
        string reportingMonth,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(reportingMonth);
        await DiscardSupersededGeneratedDraftsAsync(framework, reportingMonth, null, cancellationToken);
        return await store.ReadAsync(database => (IReadOnlyList<ReportingEvidence>)database.Evidence
            .Where(item => item.Framework == framework && item.ReportMonth == reportingMonth)
            .OrderByDescending(item => item.ArchivedAtUtc)
            .ThenByDescending(item => item.Id)
            .Select(ToReportingEvidence)
            .ToList(), cancellationToken);
    }

    public Task<IReadOnlyList<ValidationFinding>> GetReportingFindingsAsync(
        FrameworkCode framework,
        string reportingMonth,
        CancellationToken cancellationToken = default)
    {
        ValidateReportingMonth(reportingMonth);
        return store.ReadAsync(database => (IReadOnlyList<ValidationFinding>)ReportingRules.Validate(database)
            .Where(finding => IsInReportingPeriod(database, finding, framework, reportingMonth))
            .ToList(), cancellationToken);
    }

    public Task<IReadOnlyList<ReturnSubmissionHistoryItem>> GetReturnSubmissionHistoryAsync(
        Guid? returnId,
        CancellationToken cancellationToken = default)
    {
        if (returnId is not Guid id)
        {
            return Task.FromResult<IReadOnlyList<ReturnSubmissionHistoryItem>>([]);
        }

        return store.ReadAsync(database => (IReadOnlyList<ReturnSubmissionHistoryItem>)database.AuditEvents
            .Where(item => item.EntityType == "MonthlyReturn" && item.EntityId == id &&
                item.Action is "ReturnSubmitted" or "NilReturnRecorded")
            .OrderByDescending(item => item.OccurredAtUtc)
            .Select(item => new ReturnSubmissionHistoryItem(
                item.OccurredAtUtc,
                item.Action == "NilReturnRecorded",
                item.Reason))
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
        string workbookName,
        Stream workbook,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
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
            foreach (var existing in database.MiTemplates.Where(item => item.Framework == framework && item.IsActive).ToList())
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
                evidence.Id,
                workbookName,
                true,
                now);
            database.MiTemplates.Add(template);
            RecordAudit(database, now, "TemplateRegistered", "MiTemplate", template.Id, $"Registered {Frameworks.Get(framework).DisplayName} template workbook {workbookName}.", null, actor);
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
            var contracts = ContractsForReportingMonth(database, framework, reportingMonth);
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
        var fileName = $"{Frameworks.Get(framework).AgreementNumber}-MI-{reportingMonth}.xlsx";
        var archived = await evidenceArchive.ArchiveAsync(
            new EvidenceArchiveRequest(
                fileName,
                Path.Combine("generated-returns", Frameworks.Get(framework).AgreementNumber, reportingMonth, fileName),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                content),
            cancellationToken);

        var exportedReturn = await store.UpdateAsync(database =>
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
            var monthlyReturn = EnsureReturn(database, framework, reportingMonth, fileName, now);
            database.Evidence.Add(evidence);
            var returnIndex = database.MonthlyReturns.FindIndex(item => item.Id == monthlyReturn.Id);
            database.MonthlyReturns[returnIndex] = monthlyReturn with { OriginalWorkbookName = fileName, UpdatedAtUtc = now };
            RecordAudit(database, now, "ReturnExported", "MonthlyReturn", monthlyReturn.Id, $"Generated {fileName} using approved workbook {exportContext.Template.WorkbookName}.", null, actor);
            return new ExportedReturn(evidence.Id, fileName, generated.Findings);
        }, cancellationToken);

        await DiscardSupersededGeneratedDraftsAsync(framework, reportingMonth, exportedReturn.EvidenceId, cancellationToken);
        return exportedReturn;
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
        DateTimeOffset? submittedAtUtc = null,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        UpdateReturnAsync(framework, reportingMonth, ReturnStatus.Submitted, submissionReference, submittedAtUtc, null, actor, cancellationToken);

    public Task<ReturnActionResult> MarkNilReturnAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? submissionReference = null,
        DateTimeOffset? submittedAtUtc = null,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        UpdateReturnAsync(framework, reportingMonth, ReturnStatus.NilReturn, submissionReference, submittedAtUtc, null, actor, cancellationToken);

    public Task<ReturnActionResult> UpdateSubmissionReferenceAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? submissionReference,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(submissionReference))
        {
            return Task.FromResult(new ReturnActionResult(false, "Enter the GCA task reference before saving it.", []));
        }

        return UpdateSubmissionDetailsInternalAsync(
            framework,
            reportingMonth,
            submissionReference,
            null,
            "SubmissionReferenceUpdated",
            $"{Frameworks.Get(framework).DisplayName} {reportingMonth} GCA task reference was recorded.",
            "The GCA task reference has been saved.",
            actor,
            cancellationToken);
    }

    public Task<ReturnActionResult> UpdateSubmissionDetailsAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? submissionReference,
        DateTimeOffset? submittedAtUtc,
        string? actor = null,
        CancellationToken cancellationToken = default) =>
        UpdateSubmissionDetailsInternalAsync(
            framework,
            reportingMonth,
            submissionReference,
            submittedAtUtc,
            "SubmissionDetailsUpdated",
            $"{Frameworks.Get(framework).DisplayName} {reportingMonth} submission details were updated.",
            "The submission details have been saved.",
            actor,
            cancellationToken);

    public async Task<ReturnActionResult> UpdateSubmissionEvidenceAsync(
        Guid monthlyReturnId,
        IReadOnlyList<SubmissionEvidenceEdit> edits,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var update = await store.UpdateAsync(database =>
        {
            var monthlyReturn = database.MonthlyReturns.SingleOrDefault(item => item.Id == monthlyReturnId);
            if (monthlyReturn is null || monthlyReturn.Status is not (ReturnStatus.Submitted or ReturnStatus.NilReturn or ReturnStatus.CorrectionRequired))
            {
                return new SubmissionEvidenceUpdateResult(
                    new ReturnActionResult(false, "Record the submission before editing its evidence.", []),
                    []);
            }

            var distinctEdits = edits
                .GroupBy(item => item.EvidenceId)
                .Select(group => group.Last())
                .ToList();
            var submissionEvidence = database.Evidence
                .Where(item => item.Kind == EvidenceKind.SubmissionEvidence &&
                    item.Framework == monthlyReturn.Framework &&
                    item.ReportMonth == monthlyReturn.ReportMonth)
                .ToDictionary(item => item.Id);
            if (distinctEdits.Any(edit => !submissionEvidence.ContainsKey(edit.EvidenceId)))
            {
                return new SubmissionEvidenceUpdateResult(
                    new ReturnActionResult(false, "One or more submission documents are no longer available. Reload the return and try again.", []),
                    []);
            }

            foreach (var edit in distinctEdits.Where(item => !item.Remove))
            {
                if (!TryCreateEvidenceFileName(submissionEvidence[edit.EvidenceId].FileName, edit.Title, out _))
                {
                    return new SubmissionEvidenceUpdateResult(
                        new ReturnActionResult(false, "Give each retained submission document a valid title before saving.", []),
                        []);
                }
            }

            var now = timeProvider.GetUtcNow();
            var removed = new List<EvidenceRecord>();
            foreach (var edit in distinctEdits)
            {
                var existing = submissionEvidence[edit.EvidenceId];
                if (edit.Remove)
                {
                    database.Evidence.Remove(existing);
                    removed.Add(existing);
                    RecordAudit(
                        database,
                        now,
                        "SubmissionEvidenceDeleted",
                        "MonthlyReturn",
                        monthlyReturn.Id,
                        $"Deleted submission evidence {existing.FileName} for {Frameworks.Get(monthlyReturn.Framework).DisplayName} {monthlyReturn.ReportMonth}.",
                        null,
                        actor);
                    continue;
                }

                _ = TryCreateEvidenceFileName(existing.FileName, edit.Title, out var fileName);
                if (string.Equals(existing.FileName, fileName, StringComparison.Ordinal))
                {
                    continue;
                }

                database.Evidence[database.Evidence.IndexOf(existing)] = existing with { FileName = fileName };
                RecordAudit(
                    database,
                    now,
                    "SubmissionEvidenceRenamed",
                    "MonthlyReturn",
                    monthlyReturn.Id,
                    $"Renamed submission evidence from {existing.FileName} to {fileName} for {Frameworks.Get(monthlyReturn.Framework).DisplayName} {monthlyReturn.ReportMonth}.",
                    null,
                    actor);
            }

            var unreferencedEvidence = removed
                .Where(item => !database.Evidence.Any(remaining => string.Equals(
                    remaining.StoredRelativePath,
                    item.StoredRelativePath,
                    StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return new SubmissionEvidenceUpdateResult(
                new ReturnActionResult(true, "The submission documents have been saved.", [], monthlyReturn.Id),
                unreferencedEvidence);
        }, cancellationToken);

        if (!update.Result.Succeeded)
        {
            return update.Result;
        }

        var evidenceCleanupSucceeded = true;
        foreach (var evidence in update.UnreferencedEvidence)
        {
            try
            {
                await evidenceArchive.DeleteAsync(evidence, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                evidenceCleanupSucceeded = false;
            }
        }

        return evidenceCleanupSucceeded
            ? update.Result
            : update.Result with { Message = "The submission documents were updated, but one or more deleted files could not be removed from the archive." };
    }

    private Task<ReturnActionResult> UpdateSubmissionDetailsInternalAsync(
        FrameworkCode framework,
        string reportingMonth,
        string? submissionReference,
        DateTimeOffset? submittedAtUtc,
        string auditAction,
        string auditSummary,
        string successMessage,
        string? actor,
        CancellationToken cancellationToken)
    {
        ValidateReportingMonth(reportingMonth);

        return store.UpdateAsync(database =>
        {
            var existing = database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework && item.ReportMonth == reportingMonth);
            if (existing is null || existing.Status is not (ReturnStatus.Submitted or ReturnStatus.NilReturn or ReturnStatus.CorrectionRequired))
            {
                return new ReturnActionResult(false, "Record the submission before adding its GCA task reference.", []);
            }

            var now = timeProvider.GetUtcNow();
            var replacement = existing with
            {
                SubmissionReference = string.IsNullOrWhiteSpace(submissionReference) ? null : submissionReference.Trim(),
                SubmittedAtUtc = submittedAtUtc?.ToUniversalTime() ?? existing.SubmittedAtUtc,
                UpdatedAtUtc = now,
            };
            database.MonthlyReturns[database.MonthlyReturns.FindIndex(item => item.Id == existing.Id)] = replacement;

            RecordAudit(
                database,
                now,
                auditAction,
                "MonthlyReturn",
                existing.Id,
                auditSummary,
                null,
                actor);
            return new ReturnActionResult(true, successMessage, [], existing.Id);
        }, cancellationToken);
    }

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

        return UpdateReturnAsync(framework, reportingMonth, ReturnStatus.CorrectionRequired, null, null, reason.Trim(), actor, cancellationToken);
    }

    private Task<ReturnActionResult> UpdateReturnAsync(
        FrameworkCode framework,
        string reportingMonth,
        ReturnStatus status,
        string? submissionReference,
        DateTimeOffset? submittedAtUtc,
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

            var hasActivity = ContractsForReportingMonth(database, framework, reportingMonth).Count != 0
                || database.Invoices.Any(invoice => invoice.Framework == framework && invoice.ReportMonth == reportingMonth);
            if (status == ReturnStatus.NilReturn && hasActivity)
            {
                return new ReturnActionResult(false, "This reporting period has imported activity and cannot be recorded as a nil return.", []);
            }

            var now = timeProvider.GetUtcNow();
            var existing = EnsureReturn(database, framework, reportingMonth, null, now);
            var effectiveSubmissionTime = submittedAtUtc?.ToUniversalTime() ?? now;
            var replacement = existing with
            {
                Status = status,
                SubmittedAtUtc = status switch
                {
                    ReturnStatus.Submitted or ReturnStatus.NilReturn => effectiveSubmissionTime,
                    ReturnStatus.CorrectionRequired => existing.SubmittedAtUtc,
                    _ => null,
                },
                SubmissionReference = status == ReturnStatus.CorrectionRequired
                    ? existing.SubmissionReference
                    : string.IsNullOrWhiteSpace(submissionReference) ? null : submissionReference.Trim(),
                UpdatedAtUtc = now,
            };
            database.MonthlyReturns[database.MonthlyReturns.FindIndex(item => item.Id == existing.Id)] = replacement;

            if (status == ReturnStatus.Submitted)
            {
                var alreadyReported = database.ContractReportingOccurrences
                    .Select(occurrence => occurrence.ContractId)
                    .ToHashSet();
                foreach (var contract in database.Contracts.Where(contract =>
                    contract.Framework == framework &&
                    contract.ReportMonth == reportingMonth &&
                    !alreadyReported.Contains(contract.Id)))
                {
                    database.ContractReportingOccurrences.Add(new ContractReportingOccurrence(
                        Guid.NewGuid(),
                        contract.Id,
                        replacement.Id,
                        reportingMonth,
                        effectiveSubmissionTime));
                }
            }

            var action = status switch
            {
                ReturnStatus.Submitted => "ReturnSubmitted",
                ReturnStatus.NilReturn => "NilReturnRecorded",
                ReturnStatus.CorrectionRequired => "CorrectionRequested",
                _ => "ReturnUpdated",
            };
            var auditReason = status switch
            {
                ReturnStatus.CorrectionRequired => correctionReason,
                ReturnStatus.Submitted or ReturnStatus.NilReturn => string.IsNullOrWhiteSpace(submissionReference) ? null : submissionReference.Trim(),
                _ => null,
            };
            RecordAudit(database, now, action, "MonthlyReturn", existing.Id, $"{Frameworks.Get(framework).DisplayName} {reportingMonth} status changed to {status}.", auditReason, actor);

            var message = status switch
            {
                ReturnStatus.NilReturn => "The nil return has been recorded.",
                ReturnStatus.CorrectionRequired => "The return has been marked for correction and the reason is retained in the audit trail.",
                _ => "The return has been recorded as submitted.",
            };
            return new ReturnActionResult(true, message, [], replacement.Id);
        }, cancellationToken);
    }

    private async Task DiscardSupersededGeneratedDraftsAsync(
        FrameworkCode framework,
        string reportingMonth,
        Guid? latestDraftId,
        CancellationToken cancellationToken)
    {
        var unreferencedDrafts = await store.UpdateAsync(database =>
        {
            var monthlyReturn = database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework && item.ReportMonth == reportingMonth);
            var lastSubmittedAudit = monthlyReturn is null
                ? null
                : database.AuditEvents
                    .Where(item => item.EntityType == "MonthlyReturn" && item.EntityId == monthlyReturn.Id &&
                        item.Action is "ReturnSubmitted" or "NilReturnRecorded")
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .FirstOrDefault();
            var submissionCutoff = monthlyReturn?.SubmittedAtUtc ?? lastSubmittedAudit?.OccurredAtUtc;
            var generatedDrafts = database.Evidence
                .Where(item => item.Kind == EvidenceKind.GeneratedMiWorkbook &&
                    item.Framework == framework &&
                    item.ReportMonth == reportingMonth &&
                    (submissionCutoff is null || item.ArchivedAtUtc > submissionCutoff.Value))
                .OrderByDescending(item => item.ArchivedAtUtc)
                .ThenByDescending(item => item.Id)
                .ToList();
            var retainedDraftId = latestDraftId ?? generatedDrafts.FirstOrDefault()?.Id;
            var supersededDrafts = generatedDrafts
                .Where(item => item.Id != retainedDraftId)
                .ToList();
            foreach (var supersededDraft in supersededDrafts)
            {
                database.Evidence.Remove(supersededDraft);
            }

            return (IReadOnlyList<EvidenceRecord>)supersededDrafts
                .Where(item => !database.Evidence.Any(remaining => string.Equals(remaining.StoredRelativePath, item.StoredRelativePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }, cancellationToken);

        foreach (var unreferencedDraft in unreferencedDrafts)
        {
            await evidenceArchive.DeleteAsync(unreferencedDraft, cancellationToken);
        }
    }

    private static ReportingCardModel BuildReportingCard(
        RemiDatabase database,
        FrameworkCode framework,
        string reportingMonth)
    {
        var contracts = ContractsForReportingMonth(database, framework, reportingMonth)
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
        var activeFrameworks = FrameworksForReportingMonth(database, currentReportingMonth);
        var allFindings = ReportingRules.Validate(database);
        var findings = allFindings
            .Where(finding => activeFrameworks.Any(framework => IsInReportingPeriod(database, finding, framework.Code, currentReportingMonth)))
            .ToList();
        var summaries = activeFrameworks.Select(framework => new FrameworkSummary(
            framework,
            database.Contracts.Count(contract => contract.Framework == framework.Code),
            database.Invoices.Count(invoice => invoice.Framework == framework.Code),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.Submitted),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.Draft),
            database.MonthlyReturns.Count(item => item.Framework == framework.Code && item.Status == ReturnStatus.NilReturn),
            database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework.Code && item.ReportMonth == currentReportingMonth)?.Status)).ToList();
        var readiness = activeFrameworks.Select(framework =>
        {
            var frameworkFindings = findings
                .Where(finding => IsInReportingPeriod(database, finding, framework.Code, currentReportingMonth))
                .ToList();
            return new FrameworkReadiness(
                framework,
                ContractsForReportingMonth(database, framework.Code, currentReportingMonth).Count,
                database.Invoices.Count(invoice => invoice.Framework == framework.Code && invoice.ReportMonth == currentReportingMonth),
                database.MonthlyReturns.SingleOrDefault(item => item.Framework == framework.Code && item.ReportMonth == currentReportingMonth)?.Status,
                frameworkFindings.Count(finding => finding.Severity == FindingSeverity.Error),
                frameworkFindings.Count(finding => finding.Severity == FindingSeverity.Warning));
        }).ToList();

        var invoicesByContract = database.Invoices.ToLookup(invoice =>
            (invoice.Framework, ReportingRules.NormaliseReference(invoice.SupplierReference)));
        var schedulesByContract = database.ChargeScheduleItems.ToLookup(item => item.ContractId);
        var legacyByContract = database.InvoicePlanItems.ToLookup(item => item.ContractId);
        var changesByContract = database.ContractChanges.ToLookup(item => item.ContractId);
        var progress = database.Contracts.Select(contract =>
        {
            var invoices = invoicesByContract[(contract.Framework, ReportingRules.NormaliseReference(contract.SupplierReference))];
            var reportedInvoiceValue = invoices.Sum(invoice => invoice.TotalCostExVat);
            var reportedInvoiceCount = invoices.Count();
            var legacyPlan = legacyByContract[contract.Id].ToList();
            var chargeSchedule = schedulesByContract[contract.Id]
                .OrderBy(item => item.ContractYear)
                .ThenBy(item => item.ExpectedInvoiceDate)
                .ThenBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var commercial = ContractPortfolioRules.CommercialPosition(contract, changesByContract[contract.Id].ToList());
            var comparisonValue = ContractPortfolioRules.CommittedValue(contract, commercial, chargeSchedule, legacyPlan);
            var hasSchedule = ContractPortfolioRules.ScheduledBaseValue(chargeSchedule, legacyPlan) > 0;
            var evidence = EvidenceForContract(database, contract);
            return new ContractProgress(
                contract.Id,
                contract.Framework,
                contract.SupplierReference,
                contract.CustomerName,
                contract.CustomerUrn,
                contract.ReportMonth,
                contract.StartDate,
                commercial.EndDate,
                contract.LotNumber,
                contract.ServiceGroup,
                contract.ServiceGroupLevel2,
                contract.ServiceDescription,
                contract.OrderChannel,
                contract.DigitalMarketplaceServiceId,
                commercial.ContractValueExVat,
                reportedInvoiceCount,
                reportedInvoiceValue,
                comparisonValue,
                hasSchedule,
                comparisonValue == 0 ? 0 : reportedInvoiceValue / comparisonValue,
                chargeSchedule,
                evidence,
                commercial.UnconfirmedChangeCount);
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
        return new DashboardModel(summaries, progress, findings, attentionItems, currentReportingMonth, readiness, recentActivity,
            OperationalHomeWorkspace.Build(database, today, 180));
    }

    private static MonthlyReturnRegisterModel BuildMonthlyReturnRegister(RemiDatabase database, DateOnly today)
    {
        var currentReportingMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1).ToString("yyyy-MM");
        var reportingMonths = database.Contracts
            .Select(contract => contract.ReportMonth)
            .Concat(database.ContractChanges.Select(change => ReportingMonth(change.AgreementDate)))
            .Concat(database.Invoices.Select(invoice => invoice.ReportMonth))
            .Concat(database.MonthlyReturns.Select(monthlyReturn => monthlyReturn.ReportMonth))
            .Append(currentReportingMonth)
            .Where(IsValidReportingMonth)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(month => month, StringComparer.Ordinal)
            .ToList();
        var findings = ReportingRules.Validate(database);
        var entries = reportingMonths.SelectMany(month => BuildMonthlyReturnEntries(database, month, findings)).ToList();
        return new MonthlyReturnRegisterModel(reportingMonths, entries);
    }

    private static IReadOnlyList<MonthlyReturnRegisterEntry> BuildMonthlyReturnEntries(
        RemiDatabase database, string reportingMonth, IReadOnlyList<ValidationFinding> findings) =>
        FrameworksForReportingMonth(database, reportingMonth).Select(framework =>
            {
                var monthlyReturn = database.MonthlyReturns.SingleOrDefault(item =>
                    item.Framework == framework.Code && item.ReportMonth == reportingMonth);
                var contracts = ContractsForReportingMonth(database, framework.Code, reportingMonth);
                var invoices = database.Invoices
                    .Where(invoice => invoice.Framework == framework.Code && invoice.ReportMonth == reportingMonth)
                    .ToList();
                var frameworkFindings = findings
                    .Where(finding => IsInReportingPeriod(database, finding, framework.Code, reportingMonth))
                    .ToList();
                return new MonthlyReturnRegisterEntry(
                    framework,
                    reportingMonth,
                    ReportLifecycleFor(monthlyReturn?.Status),
                    monthlyReturn?.Status == ReturnStatus.NilReturn,
                    contracts.Count,
                    contracts.Sum(contract => contract.TotalContractValueExVat),
                    invoices.Count,
                    invoices.Sum(invoice => invoice.TotalCostExVat),
                    frameworkFindings.Count(finding => finding.Severity == FindingSeverity.Error),
                    frameworkFindings.Count(finding => finding.Severity == FindingSeverity.Warning),
                    monthlyReturn?.SubmittedAtUtc,
                    monthlyReturn is { SubmittedAtUtc: null } && IsPersistedSubmission(monthlyReturn.Status)
                        ? framework.ReportingDeadline?.Calculate(reportingMonth)
                        : null,
                    monthlyReturn?.SubmissionReference,
                    monthlyReturn?.OriginalWorkbookName,
                    monthlyReturn?.UpdatedAtUtc,
                    monthlyReturn?.Id);
            })
            .ToList();

    private static IReadOnlyList<FrameworkDefinition> FrameworksForReportingMonth(
        RemiDatabase database,
        string reportingMonth) =>
        Frameworks.All
            .Where(framework => FrameworkStartDate(database, framework) is DateOnly startDate &&
                startDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture).CompareTo(reportingMonth) <= 0)
            .ToList();

    private static DateOnly? FrameworkStartDate(RemiDatabase database, FrameworkDefinition framework) =>
        database.FrameworkConfigurations
            .SingleOrDefault(item => item.Framework == framework.Code)
            ?.StartDate
        ?? framework.DefaultStartDate;

    private static DateOnly? FrameworkEndDate(RemiDatabase database, FrameworkDefinition framework) =>
        database.FrameworkConfigurations
            .SingleOrDefault(item => item.Framework == framework.Code)
            ?.EndDate
        ?? framework.DefaultEndDate;

    private static string? ValidateContractFrameworkWindow(RemiDatabase database, ContractEntry entry)
    {
        if (entry.StartDate is not { } contractStartDate)
        {
            return null;
        }

        var framework = Frameworks.Get(entry.Framework);
        var frameworkStartDate = FrameworkStartDate(database, framework);
        var frameworkEndDate = FrameworkEndDate(database, framework);
        if (frameworkStartDate is null || frameworkEndDate is null)
        {
            return $"{framework.DisplayName} does not have a complete operational date range and cannot accept a contract record.";
        }

        return contractStartDate < frameworkStartDate || contractStartDate > frameworkEndDate
            ? $"The contract start date must be between {frameworkStartDate:dd MMM yyyy} and {frameworkEndDate:dd MMM yyyy}, while {framework.DisplayName} is in operation. Existing contracts remain reportable after the framework ends."
            : null;
    }

    private static ReportLifecycleStatus ReportLifecycleFor(ReturnStatus? status) => status switch
    {
        ReturnStatus.Submitted or ReturnStatus.NilReturn => ReportLifecycleStatus.Submitted,
        ReturnStatus.CorrectionRequired => ReportLifecycleStatus.CorrectionRequired,
        _ => ReportLifecycleStatus.Draft,
    };

    private static bool IsPersistedSubmission(ReturnStatus status) => status is
        ReturnStatus.Submitted or
        ReturnStatus.NilReturn or
        ReturnStatus.CorrectionRequired;

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
            || database.ContractChanges.Any(change => change.Id == finding.EntityId &&
                database.Contracts.Any(contract => contract.Id == change.ContractId && contract.Framework == framework) &&
                ReportingMonth(change.AgreementDate) == reportingMonth)
            || database.Invoices.Any(invoice => invoice.Id == finding.EntityId && invoice.Framework == framework && invoice.ReportMonth == reportingMonth);
    }

    private static string InvoiceKey(FrameworkCode framework, string supplierReference, string invoiceNumber, DateOnly? invoiceDate, decimal total) =>
        $"{framework}|{ReportingRules.NormaliseReference(supplierReference)}|{invoiceNumber.Trim()}|{invoiceDate:yyyy-MM-dd}|{total}";

    private static bool InvoiceMatchesContract(InvoiceEntry invoice, ContractRecord? contract) =>
        contract is not null &&
        contract.Framework == invoice.Framework &&
        ReportingRules.NormaliseReference(contract.SupplierReference) == ReportingRules.NormaliseReference(invoice.SupplierReference);

    private static string ReportingMonth(DateOnly date) => date.ToString("yyyy-MM");

    private static List<ContractRecord> ContractsForReportingMonth(
        RemiDatabase database,
        FrameworkCode framework,
        string reportingMonth)
    {
        var contracts = database.Contracts
            .Where(contract => contract.Framework == framework && contract.ReportMonth == reportingMonth)
            .ToList();
        var changedContracts = from change in database.ContractChanges
                               where ReportingMonth(change.AgreementDate) == reportingMonth
                               join contract in database.Contracts on change.ContractId equals contract.Id
                               where contract.Framework == framework
                               select contract with
                               {
                                   StartDate = change.EffectiveStartDate ?? contract.StartDate,
                                   EndDate = change.EffectiveEndDate ?? contract.EndDate,
                                   TotalContractValueExVat = change.IncrementalValueExVat,
                                   ReportMonth = reportingMonth,
                                   SourceWorkbook = $"{change.Kind} agreement recorded in Remi",
                               };
        contracts.AddRange(changedContracts);
        return contracts;
    }

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

    private static bool TryCreateEvidenceFileName(string currentFileName, string title, out string fileName)
    {
        fileName = currentFileName;
        var trimmedTitle = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmedTitle) ||
            trimmedTitle is "." or ".." ||
            trimmedTitle.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var titleWithoutExtension = Path.GetFileNameWithoutExtension(trimmedTitle).Trim();
        if (string.IsNullOrWhiteSpace(titleWithoutExtension))
        {
            return false;
        }

        fileName = $"{titleWithoutExtension}{Path.GetExtension(currentFileName)}";
        return true;
    }

    private static EvidenceLink ToEvidenceLink(EvidenceRecord evidence, bool canDelete = false) => new(
        evidence.Id,
        evidence.Kind,
        evidence.FileName,
        evidence.OriginalRelativePath,
        evidence.ContentType,
        evidence.FileSizeBytes,
        evidence.ReportMonth,
        evidence.ArchivedAtUtc,
        canDelete);

    private static ReportingEvidence ToReportingEvidence(EvidenceRecord evidence) => new(
        evidence.Id,
        evidence.Kind,
        evidence.FileName,
        evidence.OriginalRelativePath,
        evidence.ContentType,
        evidence.FileSizeBytes,
        evidence.ContractReference,
        evidence.ArchivedAtUtc);

    private static IReadOnlyList<EvidenceLink> EvidenceForContract(RemiDatabase database, ContractRecord contract)
    {
        var changeIds = database.ContractChanges
            .Where(change => change.ContractId == contract.Id)
            .Select(change => change.Id)
            .ToHashSet();
        return database.Evidence
            .Where(item => item.Framework == contract.Framework &&
                (IsClipboardEvidenceFor(item, "contract", contract.Id) ||
                 changeIds.Any(changeId => IsClipboardEvidenceFor(item, "contract-change", changeId)) ||
                 (!IsClipboardEvidence(item) && (string.Equals(
                    ReportingRules.NormaliseReference(item.ContractReference ?? string.Empty),
                    ReportingRules.NormaliseReference(contract.SupplierReference),
                    StringComparison.Ordinal) ||
                 (item.ReportMonth == contract.ReportMonth &&
                    string.Equals(item.FileName, contract.SourceWorkbook, StringComparison.OrdinalIgnoreCase))))))
            .OrderBy(item => item.OriginalRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToEvidenceLink(item, IsDeletableContractEvidence(item, contract.Id, changeIds)))
            .ToList();
    }

    private static IReadOnlyList<EvidenceLink> EvidenceForInvoice(RemiDatabase database, InvoiceRecord invoice) =>
        database.Evidence
            .Where(item => item.Framework == invoice.Framework &&
                (IsClipboardEvidenceFor(item, "invoice", invoice.Id) ||
                 (!IsClipboardEvidence(item) && (string.Equals(
                    ReportingRules.NormaliseReference(item.ContractReference ?? string.Empty),
                    ReportingRules.NormaliseReference(invoice.SupplierReference),
                    StringComparison.Ordinal) ||
                 (item.ReportMonth == invoice.ReportMonth &&
                    string.Equals(item.FileName, invoice.SourceWorkbook, StringComparison.OrdinalIgnoreCase))))))
            .OrderBy(item => item.OriginalRelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToEvidenceLink(item))
            .ToList();

    private static bool IsClipboardEvidence(EvidenceRecord evidence) => evidence.OriginalRelativePath.StartsWith("clipboard/", StringComparison.OrdinalIgnoreCase);
    private static bool IsClipboardEvidenceFor(EvidenceRecord evidence, string entityType, Guid entityId) =>
        evidence.OriginalRelativePath.StartsWith($"clipboard/{entityType}/{entityId:D}/", StringComparison.OrdinalIgnoreCase);
    private static bool IsDeletableContractEvidence(EvidenceRecord evidence, Guid contractId, IReadOnlySet<Guid> changeIds) =>
        IsClipboardEvidenceFor(evidence, "contract", contractId) ||
        changeIds.Any(changeId => IsClipboardEvidenceFor(evidence, "contract-change", changeId));

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
                null,
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

    private static ContractServicePart EnsureDefaultContractServicePart(
        RemiDatabase database,
        ContractRecord contract,
        DateTimeOffset now)
    {
        var existing = database.ContractServiceParts
            .Where(part => part.ContractId == contract.Id)
            .OrderBy(part => part.SortOrder)
            .FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        var name = NullIfWhiteSpace(contract.ServiceDescription)
            ?? NullIfWhiteSpace(contract.ServiceGroup)
            ?? "Whole contract";
        var created = new ContractServicePart(Guid.NewGuid(), contract.Id, name, contract.StartDate, 0, now);
        database.ContractServiceParts.Add(created);
        return created;
    }

    private static void AddManualPaymentScheduleItems(
        RemiDatabase database,
        ContractRecord contract,
        ContractPaymentPlanEntry paymentPlan,
        DateTimeOffset now)
    {
        foreach (var position in paymentPlan.Positions)
        {
            database.ChargeScheduleItems.Add(new ChargeScheduleItem(
                Guid.NewGuid(),
                contract.Id,
                null,
                position.ContractYear,
                position.Description.Trim(),
                position.ExpectedInvoiceDate,
                position.ValueExVat,
                position.IsOptionalExtension,
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

        if (paymentPlan.Positions.All(position => position.IsOptionalExtension))
        {
            return "Add at least one non-optional payment position so the initial reportable contract value can be calculated.";
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
        $"{PaymentPlanTerm(paymentPlan)} term; {string.Join(" + ", paymentPlan.Positions.OrderBy(position => position.ContractYear).ThenBy(position => position.Description, StringComparer.OrdinalIgnoreCase).Select(position => $"Y{position.ContractYear} {position.Description}: {position.ValueExVat:0.00}{(position.IsOptionalExtension ? " optional" : string.Empty)}"))}";

    private static string PaymentPositionDescription(ContractPaymentPosition position)
    {
        var description = position.ContractYear == 1 && position.PositionsInYear > 1
            ? position.PositionInYear switch
            {
                1 => "Annual licence and maintenance",
                2 => "Data Migration",
                3 => "Training",
                _ => "Other",
            }
            : "Annual licence and maintenance";

        return position.HasUnresolvedUplift
            ? $"{description} (uplift: {UpliftDescription(position.SourceText)})"
            : description;
    }

    private static string UpliftDescription(string sourceText)
    {
        var marker = sourceText.IndexOf("up", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return "unspecified";
        }

        var uplift = sourceText[(marker + 2)..].Trim();
        if (string.IsNullOrWhiteSpace(uplift) || uplift == "%")
        {
            return "unspecified";
        }

        return string.Join(" ", uplift.Replace("+", " + ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

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

    private static ContractRecord MergeMiContractDetails(ContractRecord contract, ImportedContract source) =>
        contract with
        {
            CustomerName = Coalesce(contract.CustomerName, source.CustomerName) ?? contract.CustomerName,
            CustomerUrn = Coalesce(contract.CustomerUrn, source.CustomerUrn),
            StartDate = contract.StartDate ?? source.StartDate,
            EndDate = contract.EndDate ?? source.EndDate,
            LotNumber = Coalesce(contract.LotNumber, source.LotNumber),
            ServiceGroup = Coalesce(contract.ServiceGroup, source.ServiceGroup),
            ServiceGroupLevel2 = Coalesce(contract.ServiceGroupLevel2, source.ServiceGroupLevel2),
            ServiceDescription = Coalesce(contract.ServiceDescription, source.ServiceDescription),
            OrderChannel = Coalesce(contract.OrderChannel, source.OrderChannel),
            DigitalMarketplaceServiceId = Coalesce(contract.DigitalMarketplaceServiceId, source.DigitalMarketplaceServiceId),
        };

    private static InvoiceRecord MergeMiInvoiceDetails(InvoiceRecord invoice, ImportedInvoice source) =>
        invoice with
        {
            CustomerName = Coalesce(invoice.CustomerName, source.CustomerName) ?? invoice.CustomerName,
            CustomerUrn = Coalesce(invoice.CustomerUrn, source.CustomerUrn),
            InvoiceDate = invoice.InvoiceDate ?? source.InvoiceDate,
            LotNumber = Coalesce(invoice.LotNumber, source.LotNumber),
            ServiceGroup = Coalesce(invoice.ServiceGroup, source.ServiceGroup),
            ServiceGroupLevel2 = Coalesce(invoice.ServiceGroupLevel2, source.ServiceGroupLevel2),
            ServiceDescription = Coalesce(invoice.ServiceDescription, source.ServiceDescription),
            OrderChannel = Coalesce(invoice.OrderChannel, source.OrderChannel),
            DigitalMarketplaceServiceId = Coalesce(invoice.DigitalMarketplaceServiceId, source.DigitalMarketplaceServiceId),
            UnitOfMeasure = Coalesce(invoice.UnitOfMeasure, source.UnitOfMeasure),
            Quantity = invoice.Quantity ?? source.Quantity,
            PricePerUnitExVat = invoice.PricePerUnitExVat ?? source.PricePerUnitExVat,
            OriginalVendor = Coalesce(invoice.OriginalVendor, source.OriginalVendor),
            SubcontractorName = Coalesce(invoice.SubcontractorName, source.SubcontractorName),
        };

    private static InvoiceRecord CompleteMigratedInvoice(
        InvoiceRecord invoice,
        ContractRecord? contract,
        IReadOnlyList<InvoiceRecord> relatedInvoices)
    {
        string? RelatedValue(Func<InvoiceRecord, string?> selector) => relatedInvoices
            .Select(selector)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        decimal? RelatedNumber(Func<InvoiceRecord, decimal?> selector) => relatedInvoices
            .Select(selector)
            .FirstOrDefault(value => value is > 0);

        return invoice with
        {
            CustomerName = Coalesce(invoice.CustomerName, contract?.CustomerName) ?? invoice.CustomerName,
            CustomerUrn = Coalesce(invoice.CustomerUrn, Coalesce(contract?.CustomerUrn, RelatedValue(item => item.CustomerUrn))),
            LotNumber = Coalesce(invoice.LotNumber, Coalesce(contract?.LotNumber, RelatedValue(item => item.LotNumber))),
            ServiceGroup = Coalesce(invoice.ServiceGroup, Coalesce(contract?.ServiceGroup, RelatedValue(item => item.ServiceGroup))),
            ServiceGroupLevel2 = Coalesce(invoice.ServiceGroupLevel2, Coalesce(contract?.ServiceGroupLevel2, RelatedValue(item => item.ServiceGroupLevel2))),
            ServiceDescription = Coalesce(invoice.ServiceDescription, Coalesce(contract?.ServiceDescription, RelatedValue(item => item.ServiceDescription))),
            OrderChannel = Coalesce(invoice.OrderChannel, Coalesce(contract?.OrderChannel, RelatedValue(item => item.OrderChannel))),
            DigitalMarketplaceServiceId = Coalesce(invoice.DigitalMarketplaceServiceId, Coalesce(contract?.DigitalMarketplaceServiceId, RelatedValue(item => item.DigitalMarketplaceServiceId))),
            UnitOfMeasure = Coalesce(invoice.UnitOfMeasure, RelatedValue(item => item.UnitOfMeasure)) ?? InvoiceReportingDefaults.UnitOfMeasure,
            Quantity = invoice.Quantity ?? RelatedNumber(item => item.Quantity) ?? InvoiceReportingDefaults.Quantity,
            PricePerUnitExVat = invoice.PricePerUnitExVat ?? RelatedNumber(item => item.PricePerUnitExVat) ?? InvoiceReportingDefaults.PricePerUnitExVat(invoice.TotalCostExVat),
            OriginalVendor = Coalesce(invoice.OriginalVendor, RelatedValue(item => item.OriginalVendor)) ?? InvoiceReportingDefaults.OriginalVendor,
            SubcontractorName = Coalesce(invoice.SubcontractorName, RelatedValue(item => item.SubcontractorName)) ?? InvoiceReportingDefaults.SubcontractorName,
        };
    }

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
        template.WorkbookName,
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

    private DateOnly Today() => ContractPortfolioRules.Today(timeProvider);

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

    private sealed record InvoiceDeletionResult(
        ReturnActionResult Result,
        IReadOnlyList<EvidenceRecord> UnreferencedEvidence);

    private sealed record ContractDeletionResult(
        ReturnActionResult Result,
        IReadOnlyList<EvidenceRecord> UnreferencedEvidence);

    private sealed record ContractEvidenceDeletionResult(
        ReturnActionResult Result,
        EvidenceRecord? UnreferencedEvidence);

    private sealed record SubmissionEvidenceUpdateResult(
        ReturnActionResult Result,
        IReadOnlyList<EvidenceRecord> UnreferencedEvidence);

    private sealed record PaymentScheduleUpdate(int Added, int Relabelled);
}
