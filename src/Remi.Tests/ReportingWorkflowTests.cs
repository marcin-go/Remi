using Remi.Application;
using Remi.Domain;
using Remi.Web;
using Xunit;

namespace Remi.Tests;

public sealed class ReportingWorkflowTests
{
    [Fact]
    public void ReportingPeriodContext_uses_requested_period_and_resets_to_the_default_without_a_query()
    {
        var context = new ReportingPeriodContext(new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));

        context.Synchronise(["2026-07", "2026-05", "not-a-period"], "2026-05");

        Assert.Equal(["2026-07", "2026-05"], context.AvailablePeriods);
        Assert.Equal("2026-05", context.SelectedPeriod);

        context.Synchronise(["2026-07", "2026-05"], null);

        Assert.Equal("2026-07", context.SelectedPeriod);
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
    public void Submission_timestamp_parser_accepts_pasted_british_utc_text()
    {
        var parsed = SubmissionTimestampParser.TryParseUtc("6 August 2026 13:45 UTC", out var timestamp);

        Assert.True(parsed);
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 13, 45, 0, TimeSpan.Zero), timestamp);
        Assert.Equal("6 August 2026 13:45 UTC", SubmissionTimestampParser.FormatUtc(timestamp));
    }

    [Fact]
    public async Task Submission_record_uses_the_explicit_utc_timestamp()
    {
        var database = new RemiDatabase();
        var submittedAt = new DateTimeOffset(2026, 8, 6, 13, 45, 0, TimeSpan.Zero);

        var recorded = await Workspace(database).MarkNilReturnAsync(
            FrameworkCode.GCloud13,
            "2026-07",
            "e0303f95-3441-4d48-bd32-e028a66f87db",
            submittedAt);

        Assert.True(recorded.Succeeded);
        Assert.Equal(submittedAt, Assert.Single(database.MonthlyReturns).SubmittedAtUtc);
    }

    [Fact]
    public async Task Missing_submission_timestamp_can_be_added_without_reopening_the_return()
    {
        var returnId = Guid.NewGuid();
        var submittedAt = new DateTimeOffset(2026, 8, 6, 13, 45, 0, TimeSpan.Zero);
        var database = new RemiDatabase
        {
            MonthlyReturns =
            [
                new MonthlyReturn(returnId, FrameworkCode.GCloud13, "2026-07", ReturnStatus.NilReturn, null, "existing-task", null, DateTimeOffset.UtcNow),
            ],
        };

        var saved = await Workspace(database).UpdateSubmissionDetailsAsync(
            FrameworkCode.GCloud13,
            "2026-07",
            "existing-task",
            submittedAt);

        var monthlyReturn = Assert.Single(database.MonthlyReturns);
        Assert.True(saved.Succeeded);
        Assert.Equal(ReturnStatus.NilReturn, monthlyReturn.Status);
        Assert.Equal(submittedAt, monthlyReturn.SubmittedAtUtc);
        Assert.Contains(database.AuditEvents, item => item.Action == "SubmissionDetailsUpdated");
    }

    [Fact]
    public async Task Retained_submission_document_can_be_retitled_without_replacing_its_archived_copy()
    {
        var returnId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var submittedAt = new DateTimeOffset(2026, 8, 6, 13, 45, 0, TimeSpan.Zero);
        var original = new EvidenceRecord(
            evidenceId,
            EvidenceKind.SubmissionEvidence,
            FrameworkCode.GCloud13,
            "2026-07",
            "gca-confirmation.png",
            $"clipboard/monthly-return/{returnId:D}/gca-confirmation.png",
            "evidence/immutable-copy.png",
            "image/png",
            2048,
            new string('a', 64),
            null,
            submittedAt.AddMinutes(1));
        var database = new RemiDatabase
        {
            MonthlyReturns =
            [
                new MonthlyReturn(returnId, FrameworkCode.GCloud13, "2026-07", ReturnStatus.NilReturn, submittedAt, "existing-task", null, submittedAt),
            ],
            Evidence = [original],
        };

        var saved = await Workspace(database).UpdateSubmissionEvidenceAsync(
            returnId,
            [new SubmissionEvidenceEdit(evidenceId, "GCA nil-return confirmation")]);

        Assert.True(saved.Succeeded);
        var retained = Assert.Single(database.Evidence);
        Assert.Equal("GCA nil-return confirmation.png", retained.FileName);
        Assert.Equal(original.OriginalRelativePath, retained.OriginalRelativePath);
        Assert.Equal(original.StoredRelativePath, retained.StoredRelativePath);
        Assert.Equal(original.Sha256, retained.Sha256);
        Assert.Contains(database.AuditEvents, item =>
            item.Action == "SubmissionEvidenceRenamed" &&
            item.EntityId == returnId &&
            item.Summary.Contains("gca-confirmation.png", StringComparison.Ordinal) &&
            item.Summary.Contains("GCA nil-return confirmation.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Submission_document_can_be_deleted_from_the_record_and_archive()
    {
        var returnId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var submittedAt = new DateTimeOffset(2026, 8, 6, 13, 45, 0, TimeSpan.Zero);
        var evidence = new EvidenceRecord(
            evidenceId,
            EvidenceKind.SubmissionEvidence,
            FrameworkCode.GCloud13,
            "2026-07",
            "duplicate-confirmation.png",
            $"clipboard/monthly-return/{returnId:D}/duplicate-confirmation.png",
            "evidence/duplicate-confirmation.png",
            "image/png",
            2048,
            new string('b', 64),
            null,
            submittedAt.AddMinutes(1));
        var database = new RemiDatabase
        {
            MonthlyReturns =
            [
                new MonthlyReturn(returnId, FrameworkCode.GCloud13, "2026-07", ReturnStatus.NilReturn, submittedAt, "existing-task", null, submittedAt),
            ],
            Evidence = [evidence],
        };
        var archive = new RecordingEvidenceArchive();

        var saved = await Workspace(database, evidenceArchive: archive).UpdateSubmissionEvidenceAsync(
            returnId,
            [new SubmissionEvidenceEdit(evidenceId, string.Empty, true)]);

        Assert.True(saved.Succeeded);
        Assert.Empty(database.Evidence);
        Assert.Equal(evidenceId, Assert.Single(archive.DeletedEvidence).Id);
        Assert.Contains(database.AuditEvents, item =>
            item.Action == "SubmissionEvidenceDeleted" &&
            item.EntityId == returnId &&
            item.Summary.Contains("duplicate-confirmation.png", StringComparison.Ordinal));
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
    public async Task Contract_operations_support_staged_go_live_and_only_report_the_first_transition()
    {
        var contractId = Guid.NewGuid();
        var originalPartId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.VerticalApplicationSolutions, "MOLE-VALLEY", "2024-10")],
            ContractServiceParts =
            [
                new ContractServicePart(originalPartId, contractId, "Whole contract", null, 0, DateTimeOffset.UtcNow),
            ],
        };
        var workspace = Workspace(database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero)));

        var staged = await workspace.UpdateContractOperationsAsync(contractId,
        [
            new ContractServicePartEntry(originalPartId, "Land Charges + Building Control", new DateOnly(2026, 2, 1), 0),
            new ContractServicePartEntry(null, "Planning Management", null, 1),
        ]);

        Assert.True(staged.Succeeded);
        Assert.Equal(2, staged.Parts.Count);
        Assert.Equal([originalPartId], staged.NewlyLivePartIds);
        Assert.Contains(database.AuditEvents, item => item.Action == "ContractPartWentLive" && item.Summary.Contains("Land Charges + Building Control", StringComparison.Ordinal));

        var corrected = await workspace.UpdateContractOperationsAsync(contractId,
            staged.Parts.Select(part => new ContractServicePartEntry(
                part.Id,
                part.Name,
                part.Id == originalPartId ? new DateOnly(2026, 2, 2) : part.GoLiveDate,
                part.SortOrder)).ToList());

        Assert.True(corrected.Succeeded);
        Assert.Empty(corrected.NewlyLivePartIds);
    }

    [Fact]
    public async Task Submitting_a_return_records_the_contracts_first_reporting_occurrence_once()
    {
        var contractId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-07")],
        };
        var submittedAt = new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
        var workspace = Workspace(database, new FixedTimeProvider(submittedAt));

        var first = await workspace.MarkSubmittedAsync(FrameworkCode.GCloud14, "2026-07", "TASK-1");
        var second = await workspace.MarkSubmittedAsync(FrameworkCode.GCloud14, "2026-07", "TASK-2");

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        var occurrence = Assert.Single(database.ContractReportingOccurrences);
        Assert.Equal(contractId, occurrence.ContractId);
        Assert.Equal("2026-07", occurrence.ReportingMonth);
        Assert.Equal(submittedAt, occurrence.ReportedAtUtc);
    }

    [Fact]
    public async Task Framework_dates_use_official_defaults_and_can_be_configured_locally()
    {
        var database = new RemiDatabase
        {
            Contracts = [Contract(Guid.NewGuid(), FrameworkCode.GCloud14, "RM-001", "2026-07")],
        };
        var workspace = Workspace(database, new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)));

        var defaults = await workspace.GetFrameworkConfigurationsAsync();

        Assert.Equal(new DateOnly(2022, 11, 9), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud13).StartDate);
        Assert.Equal(new DateOnly(2024, 11, 8), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud13).EndDate);
        Assert.Equal(new DateOnly(2024, 10, 29), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud14).StartDate);
        Assert.Equal(new DateOnly(2026, 10, 28), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud14).EndDate);
        Assert.Equal(new DateOnly(2023, 3, 7), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.VerticalApplicationSolutions).StartDate);
        Assert.Equal(new DateOnly(2027, 3, 6), Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.VerticalApplicationSolutions).EndDate);
        Assert.Null(Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud15).StartDate);
        Assert.Null(Assert.Single(defaults, item => item.Framework.Code == FrameworkCode.GCloud15).EndDate);

        var saved = await workspace.UpdateFrameworkDatesAsync(FrameworkCode.GCloud15, new DateOnly(2026, 7, 15), new DateOnly(2028, 7, 14));
        var configurations = await workspace.GetFrameworkConfigurationsAsync();
        var register = await workspace.GetMonthlyReturnRegisterAsync();

        Assert.True(saved.Succeeded);
        Assert.Equal(new DateOnly(2026, 7, 15), Assert.Single(configurations, item => item.Framework.Code == FrameworkCode.GCloud15).StartDate);
        Assert.Equal(new DateOnly(2028, 7, 14), Assert.Single(configurations, item => item.Framework.Code == FrameworkCode.GCloud15).EndDate);
        Assert.Contains(register.Entries, item => item.Framework.Code == FrameworkCode.GCloud15 && item.ReportingMonth == "2026-07");
        Assert.Contains(database.AuditEvents, item => item.Action == "FrameworkDatesUpdated");
    }

    [Fact]
    public async Task Contract_start_must_fall_within_framework_dates_but_reporting_continues_after_framework_end()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(database, new FixedTimeProvider(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)));
        var outsideWindow = await workspace.CreateContractAsync(GCloudContractEntry("AFTER-END", new DateOnly(2026, 10, 29), "2026-11"));
        var valid = await workspace.CreateContractAsync(GCloudContractEntry("SIGNED-IN-WINDOW", new DateOnly(2026, 10, 28), "2026-11"));
        var register = await workspace.GetMonthlyReturnRegisterAsync();

        Assert.False(outsideWindow.Succeeded);
        Assert.Contains("between 29 Oct 2024 and 28 Oct 2026", outsideWindow.Message);
        Assert.True(valid.Succeeded, valid.Message);
        Assert.Contains(register.Entries, item => item.Framework.Code == FrameworkCode.GCloud14 && item.ReportingMonth == "2026-11");
    }

    [Fact]
    public async Task Contract_registration_derives_reportable_value_from_non_optional_payment_positions_and_saves_the_schedule()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(database, new FixedTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        var paymentPlan = new ContractPaymentPlanEntry(
            3,
            1,
            [
                new ContractPaymentPositionEntry(1, "Year 1 licence", 21600, new DateOnly(2025, 6, 1)),
                new ContractPaymentPositionEntry(2, "Year 2 licence", 18000, new DateOnly(2026, 6, 1)),
                new ContractPaymentPositionEntry(3, "Year 3 licence", 18000, new DateOnly(2027, 6, 1)),
                new ContractPaymentPositionEntry(4, "Optional year 4 licence", 18000, new DateOnly(2028, 6, 1), true),
            ]);
        var entry = GCloudContractEntry("SDE_202511_GMS", new DateOnly(2025, 6, 1), "2026-08") with
        {
            TotalContractValueExVat = 75600,
            PaymentPlan = paymentPlan,
        };

        var result = await workspace.CreateContractAsync(entry);

        Assert.True(result.Succeeded);
        var contract = Assert.Single(database.Contracts);
        Assert.Equal(57600, contract.TotalContractValueExVat);
        Assert.Equal(4, database.ChargeScheduleItems.Count);
        var optionalPosition = Assert.Single(database.ChargeScheduleItems.Where(item => item.IsOptionalExtension));
        Assert.Equal(4, optionalPosition.ContractYear);
        Assert.Equal("Optional year 4 licence", optionalPosition.Description);
        Assert.Equal(new DateOnly(2028, 6, 1), optionalPosition.ExpectedInvoiceDate);
        Assert.Equal(18000, optionalPosition.ValueExVat);
        Assert.Equal(entry.StartDate, Assert.Single(database.ContractServiceParts).GoLiveDate);
        Assert.Contains(database.AuditEvents, item => item.EntityId == contract.Id && item.Action == "ContractPaymentScheduleRecorded");
    }

    [Fact]
    public async Task Digital_marketplace_service_suggestions_can_be_configured_locally()
    {
        var database = new RemiDatabase
        {
            DigitalMarketplaceServices =
            [
                new DigitalMarketplaceService("115981361947474", "StatMap Cluster"),
                new DigitalMarketplaceService("g13-existing", "Existing G-Cloud 13 product", FrameworkCode.GCloud13),
            ],
        };
        var workspace = Workspace(database);

        var saved = await workspace.UpdateDigitalMarketplaceServicesAsync(
            FrameworkCode.GCloud13,
        [
            new DigitalMarketplaceService("g13-second", "Second G-Cloud 13 product", FrameworkCode.GCloud13),
            new DigitalMarketplaceService("g13-first", "First G-Cloud 13 product", FrameworkCode.GCloud13),
        ]);

        var gCloud13Services = await workspace.GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud13);
        var gCloud14Services = await workspace.GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud14);

        Assert.True(saved.Succeeded);
        Assert.Equal(["First G-Cloud 13 product", "Second G-Cloud 13 product"], gCloud13Services.Select(item => item.Name));
        Assert.Equal("StatMap Cluster", Assert.Single(gCloud14Services).Name);
        Assert.Contains(database.AuditEvents, item => item.Action == "DigitalMarketplaceServicesUpdated" && item.Summary.Contains("G-Cloud 13"));
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
                new ChargeScheduleItem(Guid.NewGuid(), contractId, null, 1, "Initial term", new DateOnly(2026, 1, 1), 1000, false, DateTimeOffset.UtcNow),
                new ChargeScheduleItem(Guid.NewGuid(), contractId, null, 2, "Optional extension", new DateOnly(2027, 1, 1), 1000, true, DateTimeOffset.UtcNow),
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
        var contractProgress = Assert.Single(dashboard.ContractProgress);
        Assert.Equal(new DateOnly(2027, 12, 31), contractProgress.EndDate);
        Assert.Equal(1500, contractProgress.TotalContractValueExVat);
        Assert.Equal(1500, contractProgress.ComparisonValueExVat);
        var extensionRow = Assert.Single(card.Contracts);
        Assert.Equal("500.00", Assert.Single(extensionRow.Fields, field => field.Label == "Total contract value").Value);
    }

    [Fact]
    public async Task Deleting_payment_position_removes_it_and_records_an_audit_event()
    {
        var contractId = Guid.NewGuid();
        var scheduleItemId = Guid.NewGuid();
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            ChargeScheduleItems =
            [
                new ChargeScheduleItem(scheduleItemId, contractId, null, 1, "Initial term", new DateOnly(2026, 1, 1), 1000, false, DateTimeOffset.UtcNow),
            ],
        };

        var result = await Workspace(database).DeleteChargeScheduleItemAsync(scheduleItemId, contractId);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(database.ChargeScheduleItems, item => item.Id == scheduleItemId);
        Assert.Contains(database.AuditEvents, item => item.Action == "ChargeScheduleDeleted" && item.EntityId == scheduleItemId);
    }

    [Fact]
    public async Task Contract_supporting_document_can_be_deleted_without_removing_protected_or_shared_evidence()
    {
        var contractId = Guid.NewGuid();
        var supportingDocument = new EvidenceRecord(
            Guid.NewGuid(), EvidenceKind.SupportingDocument, FrameworkCode.GCloud14, "2026-07", "duplicate.png",
            $"clipboard/contract/{contractId:D}/duplicate.png", "shared-content.png", "image/png", 100,
            "shared-hash", "RM-001", DateTimeOffset.UtcNow);
        var protectedEvidence = supportingDocument with
        {
            Id = Guid.NewGuid(),
            Kind = EvidenceKind.ContractDocument,
            FileName = "source.pdf",
            OriginalRelativePath = "imports/2026-07/source.pdf",
        };
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            Evidence = [supportingDocument, protectedEvidence],
        };
        var archive = new RecordingEvidenceArchive();
        var workspace = Workspace(database, evidenceArchive: archive);

        var details = await workspace.GetContractDetailsAsync(contractId);
        Assert.True(Assert.Single(details!.Evidence, item => item.Id == supportingDocument.Id).CanDelete);
        Assert.False(Assert.Single(details.Evidence, item => item.Id == protectedEvidence.Id).CanDelete);

        var protectedResult = await workspace.DeleteContractEvidenceAsync(contractId, protectedEvidence.Id);
        var deleted = await workspace.DeleteContractEvidenceAsync(contractId, supportingDocument.Id);

        Assert.False(protectedResult.Succeeded);
        Assert.True(deleted.Succeeded);
        Assert.Equal(protectedEvidence.Id, Assert.Single(database.Evidence).Id);
        Assert.Empty(archive.DeletedEvidence);
        Assert.Contains(database.AuditEvents, item =>
            item.Action == "ContractEvidenceDeleted" &&
            item.EntityId == contractId &&
            item.Summary.Contains("duplicate.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_invoice_removes_its_link_and_private_evidence_and_records_an_audit_event()
    {
        var contractId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var privateEvidence = new EvidenceRecord(
            Guid.NewGuid(),
            EvidenceKind.SupportingDocument,
            FrameworkCode.GCloud14,
            "2026-07",
            "invoice.pdf",
            $"clipboard/invoice/{invoiceId:D}/invoice.pdf",
            "invoice-private.pdf",
            "application/pdf",
            100,
            "private-hash",
            "RM-001",
            DateTimeOffset.UtcNow);
        var sharedEvidence = privateEvidence with
        {
            Id = Guid.NewGuid(),
            OriginalRelativePath = "imports/2026-07/return.xlsx",
            StoredRelativePath = "shared-return.xlsx",
            FileName = "return.xlsx",
        };
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            ContractChanges = [new ContractChangeRecord(changeId, contractId, ContractChangeKind.Extension, new DateOnly(2026, 7, 14), null, null, 500, true, true, null, DateTimeOffset.UtcNow)],
            Invoices = [Invoice(invoiceId, FrameworkCode.GCloud14, "RM-001", "INV-001", 250, "2026-07")],
            InvoiceContractChangeLinks = [new InvoiceContractChangeLink(invoiceId, changeId)],
            Evidence = [privateEvidence, sharedEvidence],
        };
        var archive = new RecordingEvidenceArchive();

        var result = await Workspace(database, evidenceArchive: archive).DeleteInvoiceAsync(invoiceId);

        Assert.True(result.Succeeded);
        Assert.Empty(database.Invoices);
        Assert.Empty(database.InvoiceContractChangeLinks);
        Assert.Equal(sharedEvidence, Assert.Single(database.Evidence));
        Assert.Equal(privateEvidence.Id, Assert.Single(archive.DeletedEvidence).Id);
        Assert.Contains(database.AuditEvents, item => item.Action == "InvoiceDeleted" && item.EntityId == invoiceId);
    }

    [Fact]
    public async Task Contract_deletion_removes_explicit_children_and_private_evidence_but_retains_invoices_and_shared_evidence()
    {
        var contractId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var partId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var contractEvidence = new EvidenceRecord(
            Guid.NewGuid(), EvidenceKind.SupportingDocument, FrameworkCode.GCloud14, "2026-07", "contract.pdf",
            $"clipboard/contract/{contractId:D}/contract.pdf", "contract-private.pdf", "application/pdf", 100,
            "contract-hash", "RM-001", now);
        var changeEvidence = contractEvidence with
        {
            Id = Guid.NewGuid(), FileName = "extension.pdf",
            OriginalRelativePath = $"clipboard/contract-change/{changeId:D}/extension.pdf",
            StoredRelativePath = "change-private.pdf", Sha256 = "change-hash",
        };
        var sharedEvidence = contractEvidence with
        {
            Id = Guid.NewGuid(), FileName = "return.xlsx", OriginalRelativePath = "imports/2026-07/return.xlsx",
            StoredRelativePath = "shared-return.xlsx", Sha256 = "shared-hash",
        };
        var database = new RemiDatabase
        {
            Contracts = [Contract(contractId, FrameworkCode.GCloud14, "RM-001", "2026-01")],
            Invoices = [Invoice(invoiceId, FrameworkCode.GCloud14, "RM-001", "INV-001", 250, "2026-07")],
            ContractChanges = [new ContractChangeRecord(changeId, contractId, ContractChangeKind.Extension, new DateOnly(2026, 7, 14), null, null, 500, true, true, null, now)],
            InvoiceContractChangeLinks = [new InvoiceContractChangeLink(invoiceId, changeId)],
            InvoicePlanItems = [new InvoicePlanItem(Guid.NewGuid(), contractId, "Annual invoice", new DateOnly(2026, 9, 1), 500)],
            ContractServiceParts = [new ContractServicePart(partId, contractId, "Implementation", null, 0, now)],
            ChargeScheduleItems = [new ChargeScheduleItem(Guid.NewGuid(), contractId, partId, 1, "Implementation", new DateOnly(2026, 9, 1), 500, false, now)],
            ContractReportingOccurrences = [new ContractReportingOccurrence(Guid.NewGuid(), contractId, Guid.NewGuid(), "2026-07", now)],
            Evidence = [contractEvidence, changeEvidence, sharedEvidence],
        };
        var archive = new RecordingEvidenceArchive();
        var workspace = Workspace(database, evidenceArchive: archive);

        var impact = await workspace.GetContractDeletionImpactAsync(contractId);
        var result = await workspace.DeleteContractAsync(contractId);

        Assert.NotNull(impact);
        Assert.Contains(impact.Objects, item => item.ObjectType == "Contract change");
        Assert.Contains(impact.Objects, item => item.ObjectType == "Payment schedule position");
        Assert.Contains(impact.Objects, item => item.ObjectType == "Operational part");
        Assert.Equal(2, impact.Objects.Count(item => item.ObjectType == "Supporting document"));
        Assert.True(result.Succeeded);
        Assert.Empty(database.Contracts);
        Assert.Equal(invoiceId, Assert.Single(database.Invoices).Id);
        Assert.Empty(database.ContractChanges);
        Assert.Empty(database.InvoiceContractChangeLinks);
        Assert.Empty(database.InvoicePlanItems);
        Assert.Empty(database.ContractServiceParts);
        Assert.Empty(database.ChargeScheduleItems);
        Assert.Empty(database.ContractReportingOccurrences);
        Assert.Equal(sharedEvidence, Assert.Single(database.Evidence));
        Assert.Equal([contractEvidence.Id, changeEvidence.Id], archive.DeletedEvidence.Select(item => item.Id));
        Assert.Contains(database.AuditEvents, item => item.Action == "ContractDeleted" && item.EntityId == contractId);
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

    private static ReportingWorkspace Workspace(
        RemiDatabase database,
        TimeProvider? timeProvider = null,
        IEvidenceArchive? evidenceArchive = null) =>
        new(new InMemoryStore(database), null!, null!, evidenceArchive!, null!, timeProvider ?? TimeProvider.System);

    private static ContractRecord Contract(Guid id, FrameworkCode framework, string reference, string reportMonth) =>
        new(id, framework, reference, "Example customer", "URN-001", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), framework == FrameworkCode.VerticalApplicationSolutions ? "3" : "2", framework == FrameworkCode.VerticalApplicationSolutions ? null : "Information and Communication Technology (ICT)", null, framework == FrameworkCode.VerticalApplicationSolutions ? "StatMap GIS system" : null, framework == FrameworkCode.VerticalApplicationSolutions ? "Direct Award" : null, framework == FrameworkCode.VerticalApplicationSolutions ? null : "123456", 1000, reportMonth, "test.xlsx", DateTimeOffset.UtcNow);

    private static ContractEntry GCloudContractEntry(string reference, DateOnly startDate, string reportMonth) =>
        new(
            FrameworkCode.GCloud14,
            reference,
            "Example customer",
            "URN-001",
            startDate,
            startDate.AddYears(3),
            "2",
            "Information and Communication Technology (ICT)",
            null,
            null,
            null,
            "123456",
            1000,
            reportMonth,
            "test");

    private static InvoiceRecord Invoice(Guid id, FrameworkCode framework, string reference, string number, decimal value, string reportMonth) =>
        new(id, framework, reference, "Example customer", "URN-001", new DateOnly(2026, 7, 1), number, framework == FrameworkCode.VerticalApplicationSolutions ? "3" : "2", framework == FrameworkCode.VerticalApplicationSolutions ? "Geographic Information System (GIS)" : "Information and Communication Technology (ICT)", framework == FrameworkCode.VerticalApplicationSolutions ? "Software" : null, framework == FrameworkCode.VerticalApplicationSolutions ? "StatMap GIS system" : null, null, framework == FrameworkCode.VerticalApplicationSolutions ? null : "123456", "Per Unit", 1, value, value, InvoiceReportingDefaults.OriginalVendor, InvoiceReportingDefaults.SubcontractorName, reportMonth, "test.xlsx", DateTimeOffset.UtcNow);

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) => Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) => Task.FromResult(update(database));
    }

    private sealed class RecordingEvidenceArchive : IEvidenceArchive
    {
        public List<EvidenceRecord> DeletedEvidence { get; } = [];

        public Task<ArchivedEvidenceFile> ArchiveAsync(EvidenceArchiveRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default)
        {
            DeletedEvidence.Add(evidence);
            return Task.CompletedTask;
        }
    }
}
