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
        var context = new ReportingPeriodContext(TimeProvider.System);

        context.Synchronise(["2026-07", "2026-05", "not-a-period"], "2026-05");

        Assert.Equal(["2026-07", "2026-05"], context.AvailablePeriods);
        Assert.Equal("2026-05", context.SelectedPeriod);

        context.Synchronise(["2026-07", "2026-05"], null);

        Assert.Equal("2026-05", context.SelectedPeriod);
    }

    [Fact]
    public void ReportingPeriodContext_rejects_invalid_requested_periods()
    {
        var context = new ReportingPeriodContext(TimeProvider.System);

        context.Synchronise(["2026-07", "2026-05"], "2026-18");

        Assert.Equal("2026-07", context.SelectedPeriod);
        Assert.True(ReportingPeriodContext.IsValidPeriod("2026-07"));
        Assert.False(ReportingPeriodContext.IsValidPeriod("2026-7"));
    }

    [Fact]
    public void ReportingRoutes_retains_existing_filters_when_adding_a_period()
    {
        Assert.Equal("contracts?quick=missing&period=2026-07", ReportingRoutes.WithPeriod("contracts?quick=missing", "2026-07"));
        Assert.Equal("/invoices?period=2026-07", ReportingRoutes.WithPeriod("/invoices", "2026-07"));
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

    private static ReportingWorkspace Workspace(RemiDatabase database) => new(new InMemoryStore(database), null!, null!, null!, null!, TimeProvider.System);

    private static ContractRecord Contract(Guid id, FrameworkCode framework, string reference, string reportMonth) =>
        new(id, framework, reference, "Example customer", "URN-001", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "Lot 1", null, null, null, null, null, 1000, reportMonth, "test.xlsx", DateTimeOffset.UtcNow);

    private static InvoiceRecord Invoice(Guid id, FrameworkCode framework, string reference, string number, decimal value, string reportMonth) =>
        new(id, framework, reference, "Example customer", "URN-001", new DateOnly(2026, 7, 1), number, "Lot 1", null, null, null, null, null, "each", 1, value, value, null, null, reportMonth, "test.xlsx", DateTimeOffset.UtcNow);

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) => Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) => Task.FromResult(update(database));
    }
}
