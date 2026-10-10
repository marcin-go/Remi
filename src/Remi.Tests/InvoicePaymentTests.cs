using Microsoft.Data.Sqlite;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class InvoicePaymentTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);

    [Theory]
    [InlineData(2026, 9, 10, 2026, 10, 10, false, 0)]
    [InlineData(2026, 9, 9, 2026, 10, 9, true, 1)]
    [InlineData(2024, 2, 1, 2024, 3, 2, true, 952)]
    [InlineData(2026, 12, 15, 2027, 1, 14, false, 0)]
    public void Due_dates_use_calendar_days_and_overdue_begins_the_day_after_due(int y, int m, int d, int dy, int dm, int dd, bool overdue, int days)
    {
        var invoice = Invoice(new(y, m, d)) with { PaymentStatus = InvoicePaymentStatus.Unpaid };
        Assert.Equal(new DateOnly(dy, dm, dd), invoice.DueDate);
        Assert.Equal(overdue, invoice.IsOverdue(Today));
        Assert.Equal(days, invoice.DaysOverdue(Today));
        Assert.False((invoice with { PaymentStatus = InvoicePaymentStatus.Unknown }).IsOverdue(Today));
        Assert.False((invoice with { PaymentStatus = InvoicePaymentStatus.Paid, DatePaid = Today }).IsOverdue(Today));
    }

    [Fact]
    public async Task Clearing_an_assumed_date_marks_unpaid_and_keeps_invoice_allocations_and_reporting_unchanged()
    {
        var invoice = Invoice(Today.AddDays(-40)) with { PaymentStatus = InvoicePaymentStatus.Paid, DatePaid = Today.AddDays(-25), IsPaymentDateAssumed = true };
        var allocation = new InvoicePaymentAllocation(invoice.Id, Guid.NewGuid(), BillingPositionSource.ChargeSchedule, 100, DateTimeOffset.UtcNow);
        var database = new RemiDatabase { Invoices = [invoice], InvoicePaymentAllocations = [allocation] };
        var workspace = Workspace(new MemoryStore(database));
        Assert.True((await workspace.UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Unpaid, invoice.DatePaid)).Succeeded);
        var updated = Assert.Single(database.Invoices);
        Assert.Null(updated.DatePaid);
        Assert.False(updated.IsPaymentDateAssumed);
        Assert.True(updated.IsOverdue(Today));
        Assert.Equal(invoice.ReportMonth, updated.ReportMonth);
        Assert.Equal(allocation, Assert.Single(database.InvoicePaymentAllocations));
        Assert.Equal("InvoicePaymentUpdated", Assert.Single(database.AuditEvents).Action);
        Assert.Single((await workspace.GetInvoicePaymentReportAsync()).Filter("overdue"));
        Assert.True((await workspace.UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Paid, Today)).Succeeded);
        Assert.Empty((await workspace.GetInvoicePaymentReportAsync()).Filter("unpaid"));
        Assert.Equal(Today, database.Invoices[0].DatePaid);
        Assert.True((await workspace.UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Unknown, Today)).Succeeded);
        Assert.Null(database.Invoices[0].DatePaid);
        Assert.Empty((await workspace.GetInvoicePaymentReportAsync()).Filter("overdue"));
    }

    [Fact]
    public async Task Invalid_payment_edits_leave_invoice_and_history_unchanged()
    {
        var invoice = Invoice(Today.AddDays(-40));
        var database = new RemiDatabase { Invoices = [invoice] };
        var workspace = Workspace(new MemoryStore(database));
        Assert.False((await workspace.UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Paid, null)).Succeeded);
        Assert.False((await workspace.UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Paid, Today.AddDays(1))).Succeeded);
        Assert.False((await workspace.UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Paid, invoice.InvoiceDate!.Value.AddDays(-1))).Succeeded);
        Assert.False((await workspace.UpdateInvoicePaymentAsync(invoice.Id, (InvoicePaymentStatus)99, null)).Succeeded);
        Assert.False((await workspace.UpdateInvoicePaymentAsync(Guid.NewGuid(), InvoicePaymentStatus.Unpaid, null)).Succeeded);
        Assert.Equal(invoice, Assert.Single(database.Invoices));
        Assert.Empty(database.AuditEvents);
    }

    [Fact]
    public async Task Reports_filter_all_periods_and_do_not_treat_unknown_or_credit_notes_as_unpaid()
    {
        var overdue = Invoice(Today.AddDays(-40)) with { PaymentStatus = InvoicePaymentStatus.Unpaid, CustomerName = "=Customer, Ltd", ReportMonth = "2025-01" };
        var dueToday = Invoice(Today.AddDays(-30)) with { PaymentStatus = InvoicePaymentStatus.Unpaid };
        var unknown = Invoice(null);
        var paid = Invoice(Today.AddDays(-50)) with { PaymentStatus = InvoicePaymentStatus.Paid, DatePaid = Today.AddDays(-35), IsPaymentDateAssumed = true };
        var credit = overdue with { Id = Guid.NewGuid(), TotalCostExVat = -10 };
        var database = new RemiDatabase { Invoices = [overdue, dueToday, unknown, paid, credit] };
        var report = await Workspace(new MemoryStore(database)).GetInvoicePaymentReportAsync();
        Assert.Equal(4, report.Invoices.Count);
        Assert.Equal(2, report.Filter("unpaid").Count);
        Assert.Equal(overdue.Id, Assert.Single(report.Filter("overdue")).Id);
        Assert.Equal(unknown.Id, Assert.Single(report.Filter("unknown")).Id);
        Assert.Equal(paid.Id, Assert.Single(report.Filter("assumed")).Id);
        Assert.Single(report.Filter("overdue", "Customer,", FrameworkCode.GCloud14));
        Assert.Empty(report.Filter("overdue", framework: FrameworkCode.VerticalApplicationSolutions));
        var csv = System.Text.Encoding.UTF8.GetString(report.ExportCsv("overdue"));
        Assert.Contains("\"'=Customer, Ltd\"", csv);
        Assert.Contains("Due date", csv);
        Assert.Contains(overdue.InvoiceNumber, csv);
        Assert.DoesNotContain(paid.InvoiceNumber, csv);
    }

    [Fact]
    public async Task V10_upgrade_backfills_once_preserves_register_and_cleared_dates_survive_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "remi-data.db");
        try
        {
            var invoice = Invoice(new DateOnly(2024, 2, 20));
            var undated = Invoice(null);
            var credit = Invoice(new DateOnly(2026, 1, 1)) with { TotalCostExVat = -25 };
            var store = new SqliteRemiStore(path);
            await store.UpdateAsync(database => { database.Invoices.AddRange([invoice, undated, credit]); return true; });
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE non_framework_annual_reviews; DROP TABLE non_framework_contract_terms; ALTER TABLE invoices DROP COLUMN payment_status; ALTER TABLE invoices DROP COLUMN date_paid; ALTER TABLE invoices DROP COLUMN payment_date_assumed; DELETE FROM remi_schema_migrations WHERE version >= 11;";
                await command.ExecuteNonQueryAsync();
            }
            var upgraded = new SqliteRemiStore(path);
            var invoices = await upgraded.ReadAsync(database => database.Invoices);
            Assert.Equal(3, invoices.Count);
            Assert.Equal(invoice with { PaymentStatus = InvoicePaymentStatus.Paid, DatePaid = new DateOnly(2024, 3, 6), IsPaymentDateAssumed = true }, invoices.Single(item => item.Id == invoice.Id));
            Assert.Equal(undated, invoices.Single(item => item.Id == undated.Id));
            Assert.Equal(new DateOnly(2026, 1, 16), invoices.Single(item => item.Id == credit.Id).DatePaid);
            Assert.Equal(2, await upgraded.ReadAsync(database => database.AuditEvents.Count(item => item.Action == "InvoicePaymentDateAssumed")));
            var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "migration-backups"), "remi-data-before-schema-v11-*.db"));
            await using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
            {
                await backup.OpenAsync();
                await using var command = backup.CreateCommand();
                command.CommandText = "PRAGMA integrity_check";
                Assert.Equal("ok", await command.ExecuteScalarAsync());
                command.CommandText = "SELECT MAX(version) FROM remi_schema_migrations";
                Assert.Equal(10L, await command.ExecuteScalarAsync());
                command.CommandText = "SELECT COUNT(*) FROM invoices";
                Assert.Equal(3L, await command.ExecuteScalarAsync());
            }
            Assert.True((await Workspace(upgraded).UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Unpaid, null)).Succeeded);
            var newlyRegistered = Invoice(Today) with { PaymentStatus = InvoicePaymentStatus.Unpaid };
            await upgraded.UpdateAsync(database => { database.Invoices.Add(newlyRegistered); return true; });
            var reopened = new SqliteRemiStore(path);
            var after = await reopened.ReadAsync(database => database.Invoices);
            Assert.Null(after.Single(item => item.Id == invoice.Id).DatePaid);
            Assert.Equal(InvoicePaymentStatus.Unpaid, after.Single(item => item.Id == invoice.Id).PaymentStatus);
            Assert.Equal(newlyRegistered, after.Single(item => item.Id == newlyRegistered.Id));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "migration-backups"), "remi-data-before-schema-v11-*.db"));
            await using var verify = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await verify.OpenAsync();
            await using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", await check.ExecuteScalarAsync());
            check.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await check.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static InvoiceRecord Invoice(DateOnly? date) => new(Guid.NewGuid(), FrameworkCode.GCloud14, "REF", "Customer", "10000001",
        date, Guid.NewGuid().ToString("N"), "2", "Cloud Software", null, null, null, "123456", "Per Unit", 1, 100, 100,
        "Vendor", "N/A", "2026-09", "test", DateTimeOffset.UtcNow);
    private static ReportingWorkspace Workspace(IRemiStore store) => new(store, null!, null!, null!, null!, new Clock());
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero); public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; }
    private sealed class MemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) => Task.FromResult(reader(database));
        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) => Task.FromResult(update(database));
    }
}
