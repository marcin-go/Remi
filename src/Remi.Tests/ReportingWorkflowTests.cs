using Remi.Application;
using Remi.Domain;
using Remi.Web;
using Xunit;

namespace Remi.Tests;

public sealed class ReportingWorkflowTests
{
    [Fact]
    public void ReportingPeriodContext_uses_requested_period_and_preserves_it_without_a_query()
    {
        var context = new ReportingPeriodContext(new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));

        context.Synchronise(["2026-07", "2026-05", "not-a-period"], "2026-05");

        Assert.Equal(["2026-07", "2026-05"], context.AvailablePeriods);
        Assert.Equal("2026-05", context.SelectedPeriod);

        context.Synchronise(["2026-07", "2026-05"], null);

        Assert.Equal("2026-05", context.SelectedPeriod);
    }

    [Fact]
    public void ReportingPeriodContext_rejects_invalid_requested_periods()
    {
        var context = new ReportingPeriodContext(new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));

        context.Synchronise(["2026-07", "2026-05"], "2026-18");

        Assert.Equal("2026-07", context.SelectedPeriod);
        Assert.True(ReportingPeriodContext.IsValidPeriod("2026-07"));
        Assert.False(ReportingPeriodContext.IsValidPeriod("2026-7"));
    }

    [Fact]
    public void ReportingPeriodContext_defaults_to_the_previous_calendar_month_even_before_data_exists_for_it()
    {
        var context = new ReportingPeriodContext(new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));

        context.Synchronise(["2026-06"], null);

        Assert.Equal(["2026-07", "2026-06"], context.AvailablePeriods);
        Assert.Equal("2026-07", context.SelectedPeriod);
    }

    [Fact]
    public void ReportingRoutes_retains_existing_filters_when_adding_a_period()
    {
        Assert.Equal("contracts?quick=missing&period=2026-07", ReportingRoutes.WithPeriod("contracts?quick=missing", "2026-07"));
        Assert.Equal("/invoices?period=2026-07", ReportingRoutes.WithPeriod("/invoices", "2026-07"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public void Validation_marks_missing_contract_as_error_and_zero_invoice_as_warning()
    {
        var invoiceId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Invoices = [Invoice(invoiceId, FrameworkCode.GCloud14, "missing-contract", "INV-001", 0, "2026-07")],
        };

        var findings = ReportingRules.Validate(database);

        Assert.Contains(findings, finding => finding.Code == "InvoiceContractNotFound" && finding.Severity == FindingSeverity.Error && finding.EntityId == invoiceId);
        Assert.Contains(findings, finding => finding.Code == "ZeroValueInvoice" && finding.Severity == FindingSeverity.Warning && finding.EntityId == invoiceId);
    }

    [Fact]
    public void Contract_validation_uses_the_selected_framework_template_fields()
    {
        var gCloudId = Guid.NewGuid();
        var vasId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts =
            [
                Contract(gCloudId, FrameworkCode.GCloud14, "GC-001", "2026-07") with
                {
                    CustomerUrn = null,
                    LotNumber = "1",
                    ServiceGroup = "Information and Communication Technology (ICT)",
                    DigitalMarketplaceServiceId = null,
                },
                Contract(vasId, FrameworkCode.VerticalApplicationSolutions, "VAS-001", "2026-07") with
                {
                    ServiceDescription = null,
                    OrderChannel = "Email",
                },
            ],
        };

        var findings = ReportingRules.Validate(database);

        Assert.Contains(findings, finding => finding.EntityId == gCloudId && finding.Code == "MissingContractCustomerUrn");
        Assert.Contains(findings, finding => finding.EntityId == gCloudId && finding.Code == "MissingContractDigitalMarketplaceServiceId");
        Assert.Contains(findings, finding => finding.EntityId == gCloudId && finding.Code == "InvalidContractServiceGroup");
        Assert.Contains(findings, finding => finding.EntityId == vasId && finding.Code == "MissingContractServiceDescription");
        Assert.Contains(findings, finding => finding.EntityId == vasId && finding.Code == "InvalidContractOrderChannel");
    }

    [Fact]
    public async Task Dashboard_readiness_uses_the_selected_period_and_groups_review_findings()
    {
        var contractId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-07")],
            Invoices = [Invoice(Guid.NewGuid(), FrameworkCode.GCloud14, "RM-001", "INV-001", 0, "2026-07")],
            MonthlyReturns = [new MonthlyReturn(Guid.NewGuid(), FrameworkCode.GCloud14, "2026-07", ReturnStatus.Draft, null, null, null, DateTimeOffset.UtcNow)],
        };

        var dashboard = await Workspace(database).GetDashboardAsync("2026-07");
        var readiness = Assert.Single(dashboard.FrameworkReadiness.Where(item => item.Framework.Code == FrameworkCode.GCloud14));

        Assert.Equal("2026-07", dashboard.CurrentReportingMonth);
        Assert.Equal(1, readiness.ContractCount);
        Assert.Equal(1, readiness.InvoiceCount);
        Assert.Equal(ReturnStatus.Draft, readiness.ReturnStatus);
        Assert.Equal(0, readiness.BlockingFindingCount);
        Assert.Equal(1, readiness.ReviewFindingCount);
    }

    [Fact]
    public async Task Invoice_history_includes_its_related_contract_but_excludes_unrelated_records()
    {
        var contractId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var unrelatedId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var database = new RemiDatabase
        {
            Contracts =
            [
                Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-07"),
                Contract(unrelatedId, FrameworkCode.GCloud14, "RM-999", "2026-07"),
            ],
            Invoices = [Invoice(invoiceId, FrameworkCode.GCloud14, "RM-001", "INV-001", 100, "2026-07")],
            AuditEvents =
            [
                new AuditEvent(Guid.NewGuid(), now.AddMinutes(-2), "ContractUpdated", "Contract", contractId, "Related contract updated.", null, "test"),
                new AuditEvent(Guid.NewGuid(), now.AddMinutes(-1), "InvoiceUpdated", "Invoice", invoiceId, "Invoice updated.", null, "test"),
                new AuditEvent(Guid.NewGuid(), now, "ContractUpdated", "Contract", unrelatedId, "Unrelated contract updated.", null, "test"),
            ],
        };

        var history = await Workspace(database).GetInvoiceAuditEventsAsync(invoiceId);

        Assert.Equal(["Invoice updated.", "Related contract updated."], history.Select(item => item.Summary));
    }

    [Fact]
    public async Task Monthly_return_register_lists_frameworks_for_a_month_and_months_for_a_framework()
    {
        var julyContractId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts =
            [
                Contract(julyContractId, FrameworkCode.GCloud14, "RM-001", "2026-07"),
                Contract(Guid.NewGuid(), FrameworkCode.VerticalApplicationSolutions, "RM-002", "2026-06"),
            ],
            Invoices = [Invoice(Guid.NewGuid(), FrameworkCode.GCloud14, "RM-001", "INV-001", 100, "2026-07")],
            MonthlyReturns =
            [
                new MonthlyReturn(Guid.NewGuid(), FrameworkCode.GCloud14, "2026-07", ReturnStatus.Submitted, DateTimeOffset.UtcNow, "portal-123", "july.xlsx", DateTimeOffset.UtcNow),
                new MonthlyReturn(Guid.NewGuid(), FrameworkCode.GCloud14, "2026-06", ReturnStatus.NilReturn, null, null, null, DateTimeOffset.UtcNow),
            ],
        };
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero));

        var register = await Workspace(database, timeProvider).GetMonthlyReturnRegisterAsync();

        Assert.Equal(["2026-07", "2026-06"], register.ReportingMonths);

        var julyEntries = register.Entries.Where(item => item.ReportingMonth == "2026-07").ToList();
        Assert.Equal(Frameworks.All.Count(item => item.DefaultStartDate is DateOnly startDate && startDate <= new DateOnly(2026, 7, 31)), julyEntries.Count);
        var gCloud14July = Assert.Single(julyEntries.Where(item => item.Framework.Code == FrameworkCode.GCloud14));
        Assert.Equal(ReportLifecycleStatus.Submitted, gCloud14July.LifecycleStatus);
        Assert.Equal(1, gCloud14July.ContractCount);
        Assert.Equal(1000, gCloud14July.ContractTotalExVat);
        Assert.Equal(1, gCloud14July.InvoiceCount);
        Assert.Equal(100, gCloud14July.InvoiceTotalExVat);
        Assert.Equal("portal-123", gCloud14July.SubmissionReference);

        var gCloud14June = Assert.Single(register.Entries.Where(item => item.Framework.Code == FrameworkCode.GCloud14 && item.ReportingMonth == "2026-06"));
        Assert.Equal(ReportLifecycleStatus.Submitted, gCloud14June.LifecycleStatus);
        Assert.True(gCloud14June.IsNilReturn);
        Assert.Null(gCloud14June.SubmittedAtUtc);
        Assert.Equal(new DateOnly(2026, 7, 7), gCloud14June.InferredSubmissionDeadline);

        var gCloud14Entries = register.Entries
            .Where(item => item.Framework.Code == FrameworkCode.GCloud14)
            .Select(item => item.ReportingMonth);
        Assert.Equal(["2026-07", "2026-06"], gCloud14Entries);
    }

    [Fact]
    public async Task Nil_return_records_its_gca_task_reference_and_returns_its_identifier()
    {
        var database = new RemiDatabase();
        var taskReference = "e0303f95-3441-4d48-bd32-e028a66f87db";

        var recorded = await Workspace(database).MarkNilReturnAsync(FrameworkCode.GCloud13, "2026-07", taskReference);

        Assert.True(recorded.Succeeded);
        Assert.NotNull(recorded.EntityId);
        var monthlyReturn = Assert.Single(database.MonthlyReturns);
        Assert.Equal(recorded.EntityId, monthlyReturn.Id);
        Assert.Equal(ReturnStatus.NilReturn, monthlyReturn.Status);
        Assert.NotNull(monthlyReturn.SubmittedAtUtc);
        Assert.Equal(taskReference, monthlyReturn.SubmissionReference);
        Assert.Contains(database.AuditEvents, item => item.Action == "NilReturnRecorded" && item.EntityId == monthlyReturn.Id);
        var history = await Workspace(database).GetReturnSubmissionHistoryAsync(monthlyReturn.Id);
        Assert.Equal(taskReference, Assert.Single(history).SubmissionReference);
    }

    [Fact]
    public async Task Recorded_nil_return_can_add_its_gca_task_reference_later()
    {
        var database = new RemiDatabase
        {
            MonthlyReturns =
            [
                new MonthlyReturn(Guid.NewGuid(), FrameworkCode.GCloud13, "2026-07", ReturnStatus.NilReturn, null, null, null, DateTimeOffset.UtcNow),
            ],
        };
        var taskReference = "e0303f95-3441-4d48-bd32-e028a66f87db";

        var saved = await Workspace(database).UpdateSubmissionReferenceAsync(FrameworkCode.GCloud13, "2026-07", taskReference);

        Assert.True(saved.Succeeded);
        Assert.Equal(taskReference, Assert.Single(database.MonthlyReturns).SubmissionReference);
        Assert.Contains(database.AuditEvents, item => item.Action == "SubmissionReferenceUpdated");
    }

    [Fact]
    public async Task Reporting_findings_are_loaded_from_current_period_data_not_the_last_action()
    {
        var database = new RemiDatabase
        {
            Invoices = [Invoice(Guid.NewGuid(), FrameworkCode.GCloud14, "missing-contract", "INV-001", 100, "2026-07")],
        };

        var findings = await Workspace(database).GetReportingFindingsAsync(FrameworkCode.GCloud14, "2026-07");

        Assert.Contains(findings, item => item.Code == "InvoiceContractNotFound");
    }

    [Fact]
    public async Task Corrected_return_keeps_the_prior_submission_in_its_history()
    {
        var returnId = Guid.NewGuid();
        var originalSubmission = new DateTimeOffset(2026, 7, 7, 10, 30, 0, TimeSpan.Zero);
        var replacementSubmission = new DateTimeOffset(2026, 7, 8, 11, 45, 0, TimeSpan.Zero);
        var database = new RemiDatabase
        {
            MonthlyReturns =
            [
                new MonthlyReturn(returnId, FrameworkCode.GCloud14, "2026-06", ReturnStatus.CorrectionRequired, originalSubmission, "original-task", "june.xlsx", originalSubmission),
            ],
            AuditEvents =
            [
                new AuditEvent(Guid.NewGuid(), originalSubmission, "ReturnSubmitted", "MonthlyReturn", returnId, "Original submission.", "original-task", "test"),
            ],
        };
        var workspace = Workspace(database, new FixedTimeProvider(replacementSubmission));

        var recorded = await workspace.MarkSubmittedAsync(FrameworkCode.GCloud14, "2026-06", "replacement-task");
        var history = await workspace.GetReturnSubmissionHistoryAsync(returnId);

        Assert.True(recorded.Succeeded);
        Assert.Equal(ReturnStatus.Submitted, Assert.Single(database.MonthlyReturns).Status);
        Assert.Equal(replacementSubmission, database.MonthlyReturns.Single().SubmittedAtUtc);
        Assert.Equal(["replacement-task", "original-task"], history.Select(item => item.SubmissionReference));
    }

    [Fact]
    public async Task Framework_start_dates_use_official_defaults_and_can_be_configured_locally()
    {
        var database = new RemiDatabase
        {
            Contracts = [Contract(Guid.NewGuid(), FrameworkCode.GCloud14, "RM-001", "2026-07")],
        };
        var workspace = Workspace(database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));

        var defaults = await workspace.GetFrameworkConfigurationsAsync();

        Assert.Equal(new DateOnly(2022, 11, 9), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud13).StartDate);
        Assert.Equal(new DateOnly(2024, 10, 29), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud14).StartDate);
        Assert.Equal(new DateOnly(2023, 3, 7), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.VerticalApplicationSolutions).StartDate);
        Assert.Null(Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud15).StartDate);

        var saved = await workspace.UpdateFrameworkStartDateAsync(FrameworkCode.GCloud15, new DateOnly(2026, 7, 15));
        var configurations = await workspace.GetFrameworkConfigurationsAsync();
        var register = await workspace.GetMonthlyReturnRegisterAsync();

        Assert.True(saved.Succeeded);
        Assert.Equal(new DateOnly(2026, 7, 15), Assert.Single(configurations, item => item.Framework.Code == FrameworkCode.GCloud15).StartDate);
        Assert.Contains(register.Entries, item => item.Framework.Code == FrameworkCode.GCloud15 && item.ReportingMonth == "2026-07");
        Assert.Contains(database.AuditEvents, item => item.Action == "FrameworkStartDateUpdated");
    }

    [Fact]
    public async Task Digital_marketplace_service_suggestions_can_be_configured_locally()
    {
        var database = new RemiDatabase
        {
            DigitalMarketplaceServices =
            [
                new DigitalMarketplaceService("115981361947474", "StatMap Cluster"),
            ],
        };
        var workspace = Workspace(database);

        var saved = await workspace.UpdateDigitalMarketplaceServicesAsync(
        [
            new DigitalMarketplaceService("779097416520979", "StatMap Earthlight GIS"),
            new DigitalMarketplaceService("115981361947474", "StatMap Cluster"),
        ]);

        var services = await workspace.GetDigitalMarketplaceServicesAsync();

        Assert.True(saved.Succeeded);
        Assert.Equal(["StatMap Cluster", "StatMap Earthlight GIS"], services.Select(item => item.Name));
        Assert.Contains(database.AuditEvents, item => item.Action == "DigitalMarketplaceServicesUpdated");
    }

    [Fact]
    public async Task Agreed_extension_reports_once_in_its_agreement_month_and_optional_year_is_not_awarded_value()
    {
        var contractId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            ChargeScheduleItems =
            [
                new ChargeScheduleItem(Guid.NewGuid(), contractId, 1, "Initial term", new DateOnly(2026, 1, 1), 1000, false, DateTimeOffset.UtcNow),
                new ChargeScheduleItem(Guid.NewGuid(), contractId, 2, "Optional extension", new DateOnly(2027, 1, 1), 1000, true, DateTimeOffset.UtcNow),
            ],
        };
        var workspace = Workspace(database);

        var recorded = await workspace.RecordContractChangeAsync(new ContractChangeEntry(
            contractId,
            ContractChangeKind.Extension,
            new DateOnly(2026, 7, 14),
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 12, 31),
            500,
            true,
            true,
            "EXT-01"));
        var dashboard = await workspace.GetDashboardAsync("2026-07");
        var card = await workspace.GetReportingCardAsync(FrameworkCode.GCloud14, "2026-07");

        Assert.True(recorded.Succeeded);
        Assert.Contains("2026-07", await workspace.GetReportingPeriodsAsync());
        var readiness = Assert.Single(dashboard.FrameworkReadiness.Where(item => item.Framework.Code == FrameworkCode.GCloud14));
        Assert.Equal(1, readiness.ContractCount);
        Assert.Equal(1500, Assert.Single(dashboard.ContractProgress).ComparisonValueExVat);
        var extensionRow = Assert.Single(card.Contracts);
        Assert.Equal("500.00", Assert.Single(extensionRow.Fields, field => field.Label == "Total contract value").Value);
    }

    [Fact]
    public async Task Invoice_can_be_linked_to_an_agreed_extension_without_changing_its_mi_record()
    {
        var contractId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            ContractChanges =
            [
                new ContractChangeRecord(changeId, contractId, ContractChangeKind.Extension, new DateOnly(2026, 7, 14), null, null, 500, true, true, null, DateTimeOffset.UtcNow),
            ],
        };
        var workspace = Workspace(database);

        var recorded = await workspace.RecordInvoiceAsync(new InvoiceEntry(
            FrameworkCode.GCloud14, "RM-001", "Example customer", "URN-001", new DateOnly(2026, 7, 31), "INV-001", "Lot 1", "Cloud support", null, null, null, "123456", "Per unit", 1, 250, 250, null, null, "2026-07", "test", changeId));

        Assert.True(recorded.Succeeded);
        Assert.Contains(database.InvoiceContractChangeLinks, link => link.InvoiceId == recorded.EntityId && link.ContractChangeId == changeId);
        var invoiceDetails = await workspace.GetInvoiceDetailsAsync(recorded.EntityId!.Value);
        Assert.Equal(changeId, invoiceDetails!.ContractChange!.Id);
    }

    [Fact]
    public async Task Recorded_contract_change_can_be_confirmed_later_without_a_document()
    {
        var contractId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            ContractChanges =
            [
                new ContractChangeRecord(changeId, contractId, ContractChangeKind.Extension, new DateOnly(2026, 7, 14), null, null, 500, true, false, "Customer call", DateTimeOffset.UtcNow),
            ],
        };

        var confirmed = await Workspace(database).ConfirmContractChangeAsync(changeId);

        Assert.True(confirmed.Succeeded);
        Assert.True(Assert.Single(database.ContractChanges).IsConfirmed);
        Assert.Contains(database.AuditEvents, item => item.Action == "ContractChangeConfirmed" && item.EntityId == changeId);
    }

    [Fact]
    public async Task Confirmed_contract_change_can_be_corrected_without_breaking_its_invoice_link()
    {
        var contractId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            ContractChanges =
            [
                new ContractChangeRecord(changeId, contractId, ContractChangeKind.Extension, new DateOnly(2026, 7, 14), null, null, 500, false, true, "EXT-01", DateTimeOffset.UtcNow),
            ],
            Invoices = [Invoice(invoiceId, FrameworkCode.GCloud14, "RM-001", "INV-001", 250, "2026-07")],
            InvoiceContractChangeLinks = [new InvoiceContractChangeLink(invoiceId, changeId)],
        };
        var workspace = Workspace(database);

        var corrected = await workspace.UpdateContractChangeAsync(changeId, new ContractChangeEntry(
            contractId,
            ContractChangeKind.Extension,
            new DateOnly(2026, 7, 14),
            null,
            null,
            500,
            true,
            true,
            "EXT-01"));

        Assert.True(corrected.Succeeded);
        Assert.True(Assert.Single(database.ContractChanges).WasProvidedForInOriginalCallOff);
        Assert.Contains(database.InvoiceContractChangeLinks, link => link.InvoiceId == invoiceId && link.ContractChangeId == changeId);
        Assert.Contains(database.AuditEvents, item => item.Action == "ContractChangeUpdated" && item.EntityId == changeId);
    }

    [Fact]
    public void Payment_schedule_keeps_an_unspaced_uplift_percentage_with_its_payment_position()
    {
        var parsed = ContractPaymentScheduleNotation.Parse("2+1 years; 13 000 + 13 000 + 13 000upCPI+4% GBP");

        var schedule = Assert.IsType<ContractPaymentSchedule>(parsed.Schedule);
        var upliftPosition = Assert.Single(schedule.Positions.Where(position => position.HasUnresolvedUplift));
        Assert.Equal(3, schedule.Positions.Count);
        Assert.Equal(3, upliftPosition.ContractYear);
        Assert.Equal(13000, upliftPosition.ValueExVat);
        Assert.Equal("13 000upCPI+4%", upliftPosition.SourceText);
    }

    [Fact]
    public async Task Ledger_import_preserves_uplift_information_in_payment_position_descriptions()
    {
        var contractId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "COL_202604_LLC", "2026-04")],
        };
        var schedule = Assert.IsType<ContractPaymentSchedule>(ContractPaymentScheduleNotation.Parse("2+1 years; 13 000 + 13 000 + 13 000upCPI+4% GBP").Schedule);
        var entry = new LedgerContractScheduleEntry(
            FrameworkCode.GCloud14,
            "COL_202604_LLC",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "2026-04",
            "G-Cloud 14",
            "B19",
            schedule);

        var imported = await Workspace(database).ImportLedgerSchedulesAsync([entry]);

        Assert.Equal(3, imported.PaymentPositionsAdded);
        Assert.Contains(database.ChargeScheduleItems, item => item.Description == "Annual licence and maintenance (uplift: CPI + 4%)" && item.ValueExVat == 13000);
    }

    [Fact]
    public async Task Ledger_import_marks_an_unspecified_uplift_in_the_payment_position_description()
    {
        var contractId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.VerticalApplicationSolutions, "HAV_202510_GIS", "2025-10")],
        };
        var schedule = Assert.IsType<ContractPaymentSchedule>(ContractPaymentScheduleNotation.Parse("1+1 years; 42 800 + 42 800up% GBP").Schedule);
        var entry = new LedgerContractScheduleEntry(
            FrameworkCode.VerticalApplicationSolutions,
            "HAV_202510_GIS",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "2025-10",
            "VAS",
            "B35",
            schedule);

        await Workspace(database).ImportLedgerSchedulesAsync([entry]);

        Assert.Contains(database.ChargeScheduleItems, item => item.Description == "Annual licence and maintenance (uplift: unspecified)" && item.ValueExVat == 42800);
    }

    private static ReportingWorkspace Workspace(RemiDatabase database, TimeProvider? timeProvider = null) =>
        new(new InMemoryStore(database), null!, null!, null!, null!, timeProvider ?? TimeProvider.System);

    private static ContractRecord Contract(Guid id, FrameworkCode framework, string reference, string reportMonth) =>
        new(id, framework, reference, "Example customer", "URN-001", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), framework == FrameworkCode.VerticalApplicationSolutions ? "3" : "2", framework == FrameworkCode.VerticalApplicationSolutions ? null : "Information and Communication Technology (ICT)", null, framework == FrameworkCode.VerticalApplicationSolutions ? "StatMap GIS system" : null, framework == FrameworkCode.VerticalApplicationSolutions ? "Direct Award" : null, framework == FrameworkCode.VerticalApplicationSolutions ? null : "123456", 1000, reportMonth, "test.xlsx", DateTimeOffset.UtcNow);

    private static InvoiceRecord Invoice(Guid id, FrameworkCode framework, string reference, string number, decimal value, string reportMonth) =>
        new(id, framework, reference, "Example customer", "URN-001", new DateOnly(2026, 7, 1), number, framework == FrameworkCode.VerticalApplicationSolutions ? "3" : "2", framework == FrameworkCode.VerticalApplicationSolutions ? "Geographic Information System (GIS)" : "Information and Communication Technology (ICT)", framework == FrameworkCode.VerticalApplicationSolutions ? "Software" : null, framework == FrameworkCode.VerticalApplicationSolutions ? "StatMap GIS system" : null, null, framework == FrameworkCode.VerticalApplicationSolutions ? null : "123456", "Per Unit", 1, value, value, InvoiceReportingDefaults.OriginalVendor, InvoiceReportingDefaults.SubcontractorName, reportMonth, "test.xlsx", DateTimeOffset.UtcNow);

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) => Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) => Task.FromResult(update(database));
    }
}
