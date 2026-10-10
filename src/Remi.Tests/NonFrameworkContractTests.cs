using System.Text.Json;
using Microsoft.Data.Sqlite;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class NonFrameworkContractTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);

    [Theory]
    [InlineData(AgreementBasis.Verbal)]
    [InlineData(AgreementBasis.EmailConfirmation)]
    [InlineData(AgreementBasis.SignedDocument)]
    public async Task Non_framework_agreements_need_no_framework_fields_or_uploaded_document(AgreementBasis basis)
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var entry = Entry() with { Terms = Entry().Terms with { AgreementBasis = basis } };
        var saved = await workspace.SaveNonFrameworkContractAsync(entry);
        Assert.True(saved.Succeeded, saved.Message);
        var contract = Assert.Single(database.Contracts);
        Assert.Equal(FrameworkCode.NonFramework, contract.Framework);
        Assert.Null(contract.EndDate);
        Assert.Null(contract.CustomerUrn);
        Assert.Equal("", contract.ReportMonth);
        Assert.Empty(database.MonthlyReturns);
        Assert.Empty(database.Evidence);
        Assert.Empty(ReportingRules.Validate(database));
        Assert.Equal(10000, Assert.Single(database.ChargeScheduleItems).ValueExVat);
        Assert.Equal(ContractLifecycle.Live, Assert.Single((await workspace.GetHomeDashboardAsync(null)).Portfolio.Contracts).Lifecycle);
    }

    [Fact]
    public async Task Verbal_notes_fixed_end_dates_and_duplicate_references_are_validated()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var entry = Entry();
        Assert.False((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { AgreementNotes = " " } })).Succeeded);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { TermKind = ContractTermKind.FixedTerm } })).Succeeded);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(entry with { EndDate = Today })).Succeeded);
        Assert.True((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { TermKind = ContractTermKind.FixedTerm }, EndDate = Today.AddDays(30) })).Succeeded);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(entry with { SupplierReference = "  nf-001  " })).Succeeded);
        Assert.Single(database.Contracts);
    }

    [Theory]
    [InlineData(ContractTermKind.OpenEnded)]
    [InlineData(ContractTermKind.AnnualRolling)]
    public void Agreements_without_end_dates_remain_live_and_do_not_appear_expired(ContractTermKind kind)
    {
        var contract = Contract(Entry() with { Terms = Entry().Terms with { TermKind = kind } });
        Assert.Equal(ContractLifecycle.Live, ContractPortfolioRules.ContractLifecycleFor(contract, null, Today));
        Assert.Equal(ContractLifecycle.Live, ContractPortfolioRules.ContractLifecycleFor(contract, null, Today.AddYears(10)));
        Assert.Equal(ContractLifecycle.Future, ContractPortfolioRules.ContractLifecycleFor(contract, null, contract.StartDate!.Value.AddDays(-1)));
        Assert.False(ContractPortfolioRules.IsEndingWithin(contract.StartDate, null, Today, 180));
    }

    [Fact]
    public void Leap_day_anniversaries_stay_anchored_and_notice_is_90_calendar_days()
    {
        var contract = Contract(Entry() with { StartDate = new DateOnly(2024, 2, 29) });
        Assert.Equal(new DateOnly(2025, 2, 28), NonFrameworkContractRules.NextAnniversary(contract, new DateOnly(2024, 3, 1)));
        Assert.Equal(new DateOnly(2024, 11, 30), NonFrameworkContractRules.NextNoticeDeadline(contract, new DateOnly(2024, 3, 1)));
        Assert.Equal(new DateOnly(2028, 2, 29), NonFrameworkContractRules.NextAnniversary(contract, new DateOnly(2027, 3, 1)));
        Assert.Equal(2, NonFrameworkContractRules.ContractYear(contract, new DateOnly(2025, 2, 28)));
    }

    [Fact]
    public async Task Rolling_notice_accepts_exactly_90_days_and_rejects_late_or_non_anniversary_termination()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var entry = Entry();
        var id = (await workspace.SaveNonFrameworkContractAsync(entry)).EntityId!.Value;
        var anniversary = new DateOnly(2027, 1, 1);
        var deadline = anniversary.AddDays(-90);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { NoticeReceivedDate = deadline.AddDays(1), TerminationDate = anniversary } }, id)).Succeeded);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { NoticeReceivedDate = deadline, TerminationDate = anniversary.AddDays(1) } }, id)).Succeeded);
        Assert.True((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { NoticeReceivedDate = deadline, TerminationDate = anniversary } }, id)).Succeeded);
        var contract = Assert.Single(database.Contracts);
        Assert.Null(NonFrameworkContractRules.NextNoticeDeadline(contract, Today));
        Assert.Null(NonFrameworkContractRules.NextAnniversary(contract, Today));
        Assert.Equal(ContractLifecycle.Ended, ContractPortfolioRules.ContractLifecycleFor(contract, anniversary, anniversary.AddDays(1)));
        Assert.True((await workspace.SaveNonFrameworkContractAsync(entry, id)).Succeeded);
        Assert.Equal(deadline, NonFrameworkContractRules.NextNoticeDeadline(database.Contracts[0], Today));
    }

    [Fact]
    public async Task Annual_prices_compound_from_reviewed_values_preserve_history_and_do_not_apply_future_rates_early()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var id = (await workspace.SaveNonFrameworkContractAsync(Entry())).EntityId!.Value;
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 3, 3, "CPI year 3")).Succeeded);
        Assert.True((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 2, 3, "Agreed CPI annual rate, December 2025")).Succeeded);
        Assert.True((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 3, 2, "Agreed CPI annual rate, December 2026")).Succeeded);
        Assert.Equal(new decimal[] { 10700m, 11342m }, database.NonFrameworkAnnualReviews.Select(item => item.AnnualChargeExVat).ToArray());
        Assert.Equal(10000, database.NonFrameworkAnnualReviews[0].PreviousAnnualChargeExVat);
        Assert.Equal(10700, database.NonFrameworkAnnualReviews[1].PreviousAnnualChargeExVat);
        Assert.Equal(10700, NonFrameworkContractRules.AnnualCharge(database.Contracts[0], database.NonFrameworkAnnualReviews, Today));
        Assert.Equal(11342, NonFrameworkContractRules.AnnualCharge(database.Contracts[0], database.NonFrameworkAnnualReviews, new DateOnly(2027, 1, 1)));
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 3, 10, "Duplicate review")).Succeeded);
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 4, 1, "Too far ahead")).Succeeded);
        Assert.Equal(3, database.ChargeScheduleItems.Count);
        Assert.Equal(2, database.AuditEvents.Count(item => item.Action == "NonFrameworkAnnualReviewRecorded"));
        Assert.Equal(10000, database.Contracts[0].TotalContractValueExVat);
        Assert.Equal(10700, Assert.Single((await workspace.GetDashboardAsync()).ContractProgress).TotalContractValueExVat);
    }

    [Fact]
    public async Task Invalid_Cpi_reviews_or_reviews_after_termination_do_not_change_data()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var entry = Entry() with { Terms = Entry().Terms with { TermKind = ContractTermKind.FixedTerm }, EndDate = new DateOnly(2026, 1, 1) };
        var id = (await workspace.SaveNonFrameworkContractAsync(entry)).EntityId!.Value;
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 2, 3, "Rate")).Succeeded);
        var openId = (await workspace.SaveNonFrameworkContractAsync(Entry() with { SupplierReference = "OPEN" })).EntityId!.Value;
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(openId, 2, 3, " ")).Succeeded);
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(openId, 2, -104, "Invalid rate")).Succeeded);
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(openId, 2, decimal.MaxValue, "Overflow")).Succeeded);
        Assert.Empty(database.NonFrameworkAnnualReviews);
        Assert.Equal(2, database.ChargeScheduleItems.Count);
    }

    [Fact]
    public async Task Non_framework_invoices_use_payment_tracking_but_never_create_framework_reporting_activity()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var id = (await workspace.SaveNonFrameworkContractAsync(Entry())).EntityId!.Value;
        var invoiceEntry = new InvoiceEntry(FrameworkCode.NonFramework, "NF-001", "Customer", null, Today.AddDays(-40), "NF-INV-1",
            null, null, null, null, null, null, null, null, null, 100, null, null, "2026-09", "Manual");
        var result = await workspace.RecordInvoiceAsync(invoiceEntry);
        Assert.True(result.Succeeded, result.Message);
        var invoice = Assert.Single(database.Invoices);
        Assert.Equal("", invoice.ReportMonth);
        Assert.Equal(InvoicePaymentStatus.Unpaid, invoice.PaymentStatus);
        Assert.True(invoice.IsOverdue(Today));
        Assert.Single((await workspace.GetInvoicePaymentReportAsync()).Filter("overdue", framework: FrameworkCode.NonFramework));
        Assert.Empty(database.MonthlyReturns);
        Assert.DoesNotContain(Frameworks.All, item => item.Code == FrameworkCode.NonFramework);
        Assert.Contains(Frameworks.RegisterCategories, item => item.Code == FrameworkCode.NonFramework);
        var dashboard = await workspace.GetDashboardAsync("2026-09");
        Assert.All(dashboard.Frameworks, item => { Assert.Equal(0, item.ContractCount); Assert.Equal(0, item.InvoiceCount); });
        var card = await workspace.GetReportingCardAsync(FrameworkCode.GCloud14, "2026-09");
        Assert.Empty(card.Contracts); Assert.Empty(card.Invoices);
        Assert.Null(await workspace.ExportReturnAsync(FrameworkCode.NonFramework, "2026-09"));
        Assert.False((await workspace.MarkSubmittedAsync(FrameworkCode.NonFramework, "2026-09", "Should not submit")).Succeeded);
        Assert.False((await workspace.MarkNilReturnAsync(FrameworkCode.NonFramework, "2026-09", "Should not submit")).Succeeded);
        Assert.False((await workspace.RecordContractChangeAsync(new(id, ContractChangeKind.Extension, Today, Today, Today.AddYears(1), 100, true, true, "No MI change"))).Succeeded);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, database.ChargeScheduleItems[0].Id, BillingPositionSource.ChargeSchedule, 100)).Succeeded);
        Assert.True((await workspace.UpdateInvoicePaymentAsync(invoice.Id, InvoicePaymentStatus.Paid, Today)).Succeeded);
        Assert.True((await workspace.UpdateInvoiceAsync(invoice.Id, invoiceEntry with { TotalCostExVat = 110 })).Succeeded);
        Assert.Equal(Today, database.Invoices[0].DatePaid);
        Assert.Equal("", database.Invoices[0].ReportMonth);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(Entry() with { StartDate = new DateOnly(2025, 2, 1) }, id)).Succeeded);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(Entry() with { SupplierReference = "Changed" }, id)).Succeeded);
    }

    [Fact]
    public async Task V11_upgrade_preserves_operational_data_and_non_framework_terms_survive_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "remi-data.db");
        try
        {
            var store = new SqliteRemiStore(path);
            await store.UpdateAsync(database =>
            {
                var contract = Contract(Entry()) with { Framework = FrameworkCode.GCloud14, NonFrameworkTerms = null, EndDate = Today.AddYears(1), ReportMonth = "2026-09" };
                database.Contracts.Add(contract);
                database.Invoices.Add(new(Guid.NewGuid(), contract.Framework, contract.SupplierReference, contract.CustomerName, null,
                    Today.AddDays(-40), "KEEP-UNPAID", null, null, null, null, null, null, null, null, null, 100, null, null, "2026-09", "Keep", DateTimeOffset.UtcNow, InvoicePaymentStatus.Unpaid));
                database.Evidence.Add(new(Guid.NewGuid(), EvidenceKind.ContractDocument, contract.Framework, "2026-09", "keep.pdf", "source/keep.pdf", "keep.pdf", "application/pdf", 10, "hash", contract.SupplierReference, DateTimeOffset.UtcNow));
                return true;
            });
            File.WriteAllText(Path.Combine(root, "keep.pdf"), "original evidence");
            var before = await store.ReadAsync(database => JsonSerializer.Serialize(database));
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE non_framework_annual_reviews; DROP TABLE non_framework_contract_terms; DELETE FROM remi_schema_migrations WHERE version=12;";
                await command.ExecuteNonQueryAsync();
            }
            var upgraded = new SqliteRemiStore(path);
            Assert.Equal(before, await upgraded.ReadAsync(database => JsonSerializer.Serialize(database)));
            var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "migration-backups"), "remi-data-before-schema-v12-*.db"));
            await using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
            {
                await backup.OpenAsync();
                await using var command = backup.CreateCommand();
                command.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", await command.ExecuteScalarAsync());
                command.CommandText = "SELECT MAX(version) FROM remi_schema_migrations"; Assert.Equal(11L, await command.ExecuteScalarAsync());
                command.CommandText = "SELECT payment_status FROM invoices"; Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
            var workspace = Workspace(upgraded);
            var id = (await workspace.SaveNonFrameworkContractAsync(Entry())).EntityId!.Value;
            Assert.True((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 2, 3, "December 2025 CPI")).Succeeded);
            var reopened = new SqliteRemiStore(path);
            var details = await Workspace(reopened).GetContractDetailsAsync(id);
            Assert.Equal(Entry().Terms, details!.Contract.NonFrameworkTerms);
            Assert.Equal(10700, Assert.Single(details.AnnualReviews).AnnualChargeExVat);
            Assert.Equal(2, details.PaymentPositions.Count);
            Assert.Equal("original evidence", File.ReadAllText(Path.Combine(root, "keep.pdf")));
            Assert.Null(await reopened.ReadAsync(database => database.Invoices[0].DatePaid));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "migration-backups"), "remi-data-before-schema-v12-*.db"));
            await using var verify = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await verify.OpenAsync();
            await using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", await check.ExecuteScalarAsync());
            check.CommandText = "PRAGMA foreign_key_check"; await using var reader = await check.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Valid_notice_cancels_future_positions_without_erasing_review_history_and_withdrawal_reopens_them()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var entry = Entry();
        var id = (await workspace.SaveNonFrameworkContractAsync(entry)).EntityId!.Value;
        Assert.True((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 2, 3, "CPI year 2")).Succeeded);
        Assert.True((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 3, 2, "CPI year 3")).Succeeded);
        var end = new DateOnly(2027, 1, 1);
        Assert.True((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { NoticeReceivedDate = end.AddDays(-90), TerminationDate = end } }, id)).Succeeded);
        var details = await workspace.GetContractDetailsAsync(id);
        var futureId = database.ChargeScheduleItems.Single(item => item.ContractYear == 3).Id;
        var cancelled = details!.PaymentPositions.Single(item => item.PositionId == futureId);
        Assert.True(cancelled.IsCancelled);
        Assert.False(cancelled.IsEligibleForBilling);
        Assert.Equal(2, details.AnnualReviews.Count);
        var portfolio = (await workspace.GetHomeDashboardAsync(null)).Portfolio;
        Assert.Empty(portfolio.MatchingPositions(new DateOnly(2027, 1, 1)));
        Assert.True((await workspace.SaveNonFrameworkContractAsync(entry, id)).Succeeded);
        details = await workspace.GetContractDetailsAsync(id);
        Assert.True(details!.PaymentPositions.Single(item => item.PositionId == futureId).IsEligibleForBilling);
        var invoice = new InvoiceRecord(Guid.NewGuid(), FrameworkCode.NonFramework, entry.SupplierReference, entry.CustomerName, null,
            Today, "ADVANCE", null, null, null, null, null, null, null, null, null, 100, null, null, "", "Test", DateTimeOffset.UtcNow);
        database.Invoices.Add(invoice);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, futureId, BillingPositionSource.ChargeSchedule, 100)).Succeeded);
        Assert.False((await workspace.SaveNonFrameworkContractAsync(entry with { Terms = entry.Terms with { NoticeReceivedDate = end.AddDays(-90), TerminationDate = end } }, id)).Succeeded);
        Assert.Null(database.Contracts[0].NonFrameworkTerms!.TerminationDate);
    }

    [Fact]
    public async Task Unchanged_annual_charges_can_be_renewed_without_Cpi_and_rounding_is_to_pennies()
    {
        var database = new RemiDatabase();
        var workspace = Workspace(new MemoryStore(database));
        var entry = Entry() with { Terms = Entry().Terms with { UsesCpi = false, AdditionalPercentage = 0, CpiReference = "" } };
        var id = (await workspace.SaveNonFrameworkContractAsync(entry)).EntityId!.Value;
        Assert.False((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 2, 3, "Not agreed")).Succeeded);
        Assert.True((await workspace.RecordNonFrameworkAnnualReviewAsync(id, 2, 0, "")).Succeeded);
        Assert.Equal(10000, Assert.Single(database.NonFrameworkAnnualReviews).AnnualChargeExVat);
        Assert.Equal(0.11m, NonFrameworkContractRules.CalculateUplift(0.10m, 5, 0));
    }

    private static NonFrameworkContractEntry Entry() => new("NF-001", "Customer", null, new DateOnly(2025, 1, 1), null,
        new(AgreementBasis.Verbal, new DateOnly(2024, 12, 1), "Agreed by Customer and Supplier: annual licence and maintenance.", ContractTermKind.AnnualRolling, 10000, true, 4, "Agreed CPI annual rate for the preceding December"));
    private static ContractRecord Contract(NonFrameworkContractEntry entry) => new(Guid.NewGuid(), FrameworkCode.NonFramework, entry.SupplierReference,
        entry.CustomerName, entry.CustomerUrn, entry.StartDate, entry.EndDate, null, null, null, null, null, null, entry.Terms.InitialAnnualChargeExVat, "", "Agreement", DateTimeOffset.UtcNow, entry.Terms);
    private static ReportingWorkspace Workspace(IRemiStore store) => new(store, null!, null!, null!, null!, new Clock());
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero); public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; }
    private sealed class MemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) => Task.FromResult(reader(database));
        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) => Task.FromResult(update(database));
    }
}
