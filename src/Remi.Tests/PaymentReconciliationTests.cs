using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class PaymentReconciliationTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_and_full_allocations_clear_only_covered_positions_and_removal_reopens_them(bool legacy)
    {
        var (database, contract, invoice, position) = Fixture(legacy);
        var store = new MemoryStore(database);
        var workspace = Workspace(store);
        var home = new OperationalHomeWorkspace(store, new Clock());
        Assert.Single((await home.GetAsync()).MatchingPositions(Today, "overdue"));
        var otherInvoice = invoice with { Id = Guid.NewGuid(), InvoiceNumber = "INV-2", TotalCostExVat = 60 };
        database.Invoices.Add(otherInvoice);

        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(legacy), 40)).Succeeded);
        var partial = Assert.Single((await home.GetAsync()).MatchingPositions(Today, "overdue"));
        Assert.Equal(60, partial.OutstandingValueExVat);
        Assert.Equal("Partially reconciled", partial.ReconciliationState);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(otherInvoice.Id, position, Source(legacy), 60)).Succeeded);
        var reconciled = await home.GetAsync();
        Assert.Empty(reconciled.MatchingPositions(Today, "overdue"));
        Assert.Equal("Reconciled", Assert.Single(reconciled.PaymentPositions).ReconciliationState);
        Assert.True((await workspace.RemoveInvoicePaymentAllocationAsync(otherInvoice.Id, position, Source(legacy))).Succeeded);
        Assert.Single((await home.GetAsync()).MatchingPositions(Today, "overdue"));
        Assert.Contains(database.AuditEvents, item => item.Action == "InvoicePaymentAllocated");
        Assert.Contains(database.AuditEvents, item => item.Action == "InvoicePaymentAllocationRemoved");
        Assert.Equal(Today.AddDays(-10), legacy ? database.InvoicePlanItems[0].ExpectedInvoiceDate : database.ChargeScheduleItems[0].ExpectedInvoiceDate);
        Assert.Equal(contract.SupplierReference, database.Invoices[0].SupplierReference);
    }

    [Fact]
    public async Task Credit_note_reopens_a_position_and_removals_cannot_create_negative_or_excessive_net_allocations()
    {
        var (database, _, invoice, position) = Fixture();
        var store = new MemoryStore(database);
        var workspace = Workspace(store);
        var credit = invoice with { Id = Guid.NewGuid(), InvoiceNumber = "CN-1", TotalCostExVat = -20 };
        var replacement = invoice with { Id = Guid.NewGuid(), InvoiceNumber = "INV-2", TotalCostExVat = 20 };
        database.Invoices.AddRange([credit, replacement]);
        Assert.False((await workspace.AllocateInvoicePaymentAsync(credit.Id, position, Source(), -20)).Succeeded);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 100)).Succeeded);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(credit.Id, position, Source(), -20)).Succeeded);
        var home = await new OperationalHomeWorkspace(store, new Clock()).GetAsync();
        Assert.Equal(20, Assert.Single(home.MatchingPositions(Today, "overdue")).OutstandingValueExVat);
        Assert.False((await workspace.RemoveInvoicePaymentAllocationAsync(invoice.Id, position, Source())).Succeeded);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(replacement.Id, position, Source(), 20)).Succeeded);
        Assert.False((await workspace.RemoveInvoicePaymentAllocationAsync(credit.Id, position, Source())).Succeeded);
        Assert.True((await workspace.RemoveInvoicePaymentAllocationAsync(replacement.Id, position, Source())).Succeeded);
        Assert.True((await workspace.RemoveInvoicePaymentAllocationAsync(credit.Id, position, Source())).Succeeded);
        Assert.True((await workspace.RemoveInvoicePaymentAllocationAsync(invoice.Id, position, Source())).Succeeded);
        Assert.Empty(database.InvoicePaymentAllocations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(0.001)]
    public async Task Invalid_amounts_do_not_change_allocations_or_audit(decimal amount)
    {
        var (database, _, invoice, position) = Fixture();
        var result = await Workspace(new MemoryStore(database)).AllocateInvoicePaymentAsync(invoice.Id, position, Source(), amount);
        Assert.False(result.Succeeded);
        Assert.Empty(database.InvoicePaymentAllocations);
        Assert.Empty(database.AuditEvents);
    }

    [Fact]
    public async Task Wrong_contract_optional_ambiguous_and_duplicate_allocations_are_rejected()
    {
        var (database, contract, invoice, position) = Fixture();
        var workspace = Workspace(new MemoryStore(database));
        var otherContract = contract with { Id = Guid.NewGuid(), SupplierReference = "OTHER" };
        database.Contracts.Add(otherContract);
        var otherPosition = database.ChargeScheduleItems[0] with { Id = Guid.NewGuid(), ContractId = otherContract.Id };
        database.ChargeScheduleItems.Add(otherPosition);
        Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, otherPosition.Id, Source(), 100)).Succeeded);
        database.ChargeScheduleItems[0] = database.ChargeScheduleItems[0] with { IsOptionalExtension = true };
        Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 100)).Succeeded);
        database.ChargeScheduleItems[0] = database.ChargeScheduleItems[0] with { IsOptionalExtension = false };
        database.Contracts.Add(contract with { Id = Guid.NewGuid() });
        Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 100)).Succeeded);
        database.Contracts.RemoveAt(database.Contracts.Count - 1);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 40)).Succeeded);
        Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 20)).Succeeded);
        Assert.Single(database.InvoicePaymentAllocations);
    }

    [Fact]
    public async Task One_invoice_can_cover_multiple_positions_but_cannot_be_overallocated()
    {
        var (database, _, invoice, position) = Fixture();
        database.ChargeScheduleItems[0] = database.ChargeScheduleItems[0] with { ValueExVat = 60 };
        var second = database.ChargeScheduleItems[0] with { Id = Guid.NewGuid(), ValueExVat = 100 };
        database.ChargeScheduleItems.Add(second);
        var workspace = Workspace(new MemoryStore(database));
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 60)).Succeeded);
        Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, second.Id, Source(), 50)).Succeeded);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, second.Id, Source(), 40)).Succeeded);
        Assert.Equal(0, (await workspace.GetInvoiceReconciliationAsync(invoice.Id))!.UnallocatedValueExVat);
    }

    [Fact]
    public async Task Allocated_records_cannot_be_deleted_or_edited_in_ways_that_invalidate_reconciliation()
    {
        var (database, contract, invoice, position) = Fixture();
        var workspace = Workspace(new MemoryStore(database));
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 100)).Succeeded);
        Assert.False((await workspace.DeleteInvoiceAsync(invoice.Id)).Succeeded);
        Assert.False((await workspace.DeleteContractAsync(contract.Id)).Succeeded);
        Assert.False((await workspace.DeleteChargeScheduleItemAsync(position, contract.Id)).Succeeded);
        Assert.False((await workspace.UpdateChargeScheduleItemAsync(position, new(contract.Id, 1, "Annual licence", Today, 90, false))).Succeeded);
        Assert.False((await workspace.UpdateChargeScheduleItemAsync(position, new(contract.Id, 1, "Annual licence", Today, 100, true))).Succeeded);
        var invoiceEntry = new InvoiceEntry(invoice.Framework, invoice.SupplierReference, invoice.CustomerName, invoice.CustomerUrn,
            invoice.InvoiceDate, invoice.InvoiceNumber, invoice.LotNumber, invoice.ServiceGroup, null, null, null,
            invoice.DigitalMarketplaceServiceId, invoice.UnitOfMeasure, invoice.Quantity, invoice.PricePerUnitExVat, 90,
            invoice.OriginalVendor, invoice.SubcontractorName, invoice.ReportMonth, "Edit");
        Assert.False((await workspace.UpdateInvoiceAsync(invoice.Id, invoiceEntry)).Succeeded);
        Assert.False((await workspace.UpdateInvoiceAsync(invoice.Id, invoiceEntry with { TotalCostExVat = 100, SupplierReference = "OTHER" })).Succeeded);
        Assert.True((await workspace.UpdateInvoiceAsync(invoice.Id, invoiceEntry with { TotalCostExVat = 100 })).Succeeded);
        Assert.Single(database.InvoicePaymentAllocations);
        Assert.Single(database.Contracts);
        Assert.Single(database.Invoices);
    }

    [Fact]
    public async Task Allocated_legacy_plan_cannot_be_hidden_by_a_replacement_charge_schedule()
    {
        var (database, contract, invoice, position) = Fixture(true);
        var workspace = Workspace(new MemoryStore(database));
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(true), 100)).Succeeded);
        Assert.False((await workspace.AddChargeScheduleItemAsync(new(contract.Id, 1, "Replacement", Today, 100, false))).Succeeded);
        Assert.Empty(database.ChargeScheduleItems);
    }

    [Fact]
    public async Task Sqlite_round_trip_preserves_allocations_through_unrelated_register_updates()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (database, contract, invoice, position) = Fixture();
            var path = Path.Combine(root, "remi-data.db");
            var store = new SqliteRemiStore(path);
            await store.UpdateAsync(target =>
            {
                target.Contracts.Add(contract);
                target.Invoices.Add(invoice);
                target.ChargeScheduleItems.Add(database.ChargeScheduleItems[0]);
                return true;
            });
            Assert.True((await Workspace(store).AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 100)).Succeeded);
            var reopened = new SqliteRemiStore(path);
            await reopened.UpdateAsync(target => { target.Contracts[0] = target.Contracts[0] with { CustomerName = "Updated name" }; return true; });
            Assert.Equal(100, Assert.Single((await Workspace(new SqliteRemiStore(path)).GetInvoiceReconciliationAsync(invoice.Id))!.Allocations).ValueExVat);
            Assert.Empty((await new OperationalHomeWorkspace(reopened, new Clock()).GetAsync()).MatchingPositions(Today, "overdue"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static BillingPositionSource Source(bool legacy = false) => legacy ? BillingPositionSource.LegacyPlan : BillingPositionSource.ChargeSchedule;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optional_position_can_be_reconciled_against_an_explicit_confirmed_extension(bool prelinked)
    {
        var (database, contract, invoice, position) = Fixture();
        database.ChargeScheduleItems[0] = database.ChargeScheduleItems[0] with { IsOptionalExtension = true };
        var anotherOptional = database.ChargeScheduleItems[0] with { Id = Guid.NewGuid(), ContractYear = 3 };
        database.ChargeScheduleItems.Add(anotherOptional);
        var extension = new ContractChangeRecord(Guid.NewGuid(), contract.Id, ContractChangeKind.Extension, Today.AddDays(-20), Today.AddDays(-10), Today.AddYears(1), 100, true, true, "EXT-1", Now);
        database.ContractChanges.Add(extension);
        if (prelinked) database.InvoiceContractChangeLinks.Add(new(invoice.Id, extension.Id));
        var store = new MemoryStore(database);
        var workspace = Workspace(store);
        if (!prelinked)
            Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 40)).Succeeded);
        Assert.True((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 40, extensionId: prelinked ? null : extension.Id)).Succeeded);
        Assert.Equal(extension.Id, Assert.Single(database.InvoiceContractChangeLinks).ContractChangeId);
        var home = await new OperationalHomeWorkspace(store, new Clock()).GetAsync();
        Assert.Equal(60, Assert.Single(home.MatchingPositions(Today, "overdue")).OutstandingValueExVat);
        Assert.False(home.PaymentPositions.Single(item => item.PositionId == anotherOptional.Id).IsEligibleForBilling);
        var entry = new ChargeScheduleEntry(contract.Id, 1, "Annual licence", Today.AddDays(-10), 100, true);
        Assert.True((await workspace.UpdateChargeScheduleItemAsync(position, entry)).Succeeded);
    }

    [Fact]
    public async Task Pending_or_other_contract_extensions_cannot_commit_optional_positions()
    {
        var (database, contract, invoice, position) = Fixture();
        database.ChargeScheduleItems[0] = database.ChargeScheduleItems[0] with { IsOptionalExtension = true };
        var extension = new ContractChangeRecord(Guid.NewGuid(), contract.Id, ContractChangeKind.Extension, Today, Today, Today.AddYears(1), 100, true, false, "EXT", Now);
        database.ContractChanges.Add(extension);
        var workspace = Workspace(new MemoryStore(database));
        Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 100, extensionId: extension.Id)).Succeeded);
        database.ContractChanges[0] = extension with { IsConfirmed = true, ContractId = Guid.NewGuid() };
        Assert.False((await workspace.AllocateInvoicePaymentAsync(invoice.Id, position, Source(), 100, extensionId: extension.Id)).Succeeded);
        Assert.Empty(database.InvoicePaymentAllocations);
        Assert.Empty(database.InvoiceContractChangeLinks);
    }
    private static ReportingWorkspace Workspace(IRemiStore store) => new(store, null!, null!, null!, null!, new Clock());
    private static (RemiDatabase Database, ContractRecord Contract, InvoiceRecord Invoice, Guid Position) Fixture(bool legacy = false)
    {
        var contract = new ContractRecord(Guid.NewGuid(), FrameworkCode.GCloud14, "REF", "Customer", "10000001",
            Today.AddMonths(-6), Today.AddMonths(6), "2", "Cloud Software", null, null, null, "123456", 100,
            "2026-09", "test", Now);
        var invoice = new InvoiceRecord(Guid.NewGuid(), contract.Framework, contract.SupplierReference, contract.CustomerName,
            contract.CustomerUrn, Today, "INV-1", "2", "Cloud Software", null, null, null, "123456", "Per Unit", 1, 100, 100,
            "Vendor", "N/A", "2026-09", "test", Now);
        var position = Guid.NewGuid();
        var database = new RemiDatabase { Contracts = [contract], Invoices = [invoice] };
        if (legacy) database.InvoicePlanItems.Add(new(position, contract.Id, "Annual licence", Today.AddDays(-10), 100));
        else database.ChargeScheduleItems.Add(new(position, contract.Id, null, 1, "Annual licence", Today.AddDays(-10), 100, false, Now));
        return (database, contract, invoice, position);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; }
    private sealed class MemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) => Task.FromResult(reader(database));
        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) => Task.FromResult(update(database));
    }
}
