using Remi.Domain;

namespace Remi.Application;

public sealed record InvoiceReconciliationModel(
    InvoiceRecord Invoice, ContractRecord? Contract, IReadOnlyList<ScheduledPaymentPosition> Positions,
    IReadOnlyList<InvoicePaymentAllocation> Allocations,
    IReadOnlyList<ContractChangeRecord> ConfirmedExtensions, Guid? LinkedExtensionId)
{
    public decimal UnallocatedValueExVat => Invoice.TotalCostExVat - Allocations.Sum(item => item.ValueExVat);
}

public sealed partial class ReportingWorkspace
{
    internal static HashSet<(Guid PositionId, BillingPositionSource Source)> CommittedExtensionPositions(RemiDatabase database)
    {
        var confirmed = database.ContractChanges.Where(item => item.Kind == ContractChangeKind.Extension && item.IsConfirmed).Select(item => item.Id).ToHashSet();
        var invoices = database.InvoiceContractChangeLinks.Where(item => confirmed.Contains(item.ContractChangeId)).Select(item => item.InvoiceId).ToHashSet();
        return database.InvoicePaymentAllocations.Where(item => invoices.Contains(item.InvoiceId)).Select(item => (item.PositionId, item.Source)).ToHashSet();
    }

    internal static ContractRecord? ReconciliationContract(RemiDatabase database, InvoiceRecord invoice)
    {
        var matches = database.Contracts.Where(contract => contract.Framework == invoice.Framework &&
            ReportingRules.NormaliseReference(contract.SupplierReference) == ReportingRules.NormaliseReference(invoice.SupplierReference)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static IReadOnlyList<ScheduledPaymentPosition> ReconciliationPositions(RemiDatabase database, ContractRecord contract)
    {
        var committed = CommittedExtensionPositions(database);
        var schedule = database.ChargeScheduleItems.Where(item => item.ContractId == contract.Id).ToList();
        IEnumerable<ScheduledPaymentPosition> positions = schedule.Count > 0
            ? schedule.Select(item => new ScheduledPaymentPosition(item.Id, contract.Id, contract.SupplierReference,
                contract.CustomerName, item.Description, item.ExpectedInvoiceDate, item.ValueExVat, item.IsOptionalExtension, BillingPositionSource.ChargeSchedule))
            : database.InvoicePlanItems.Where(item => item.ContractId == contract.Id).Select(item => new ScheduledPaymentPosition(
                item.Id, contract.Id, contract.SupplierReference, contract.CustomerName, item.Label, item.ExpectedInvoiceDate,
                item.ExpectedValueExVat, false, BillingPositionSource.LegacyPlan));
        return positions.Select(position => position with
        {
            AllocatedValueExVat = database.InvoicePaymentAllocations.Where(item => item.PositionId == position.PositionId && item.Source == position.Source).Sum(item => item.ValueExVat),
            IsExtensionCommitted = committed.Contains((position.PositionId, position.Source)),
        }).OrderBy(item => item.ExpectedInvoiceDate).ThenBy(item => item.Description).ToList();
    }

    public Task<InvoiceReconciliationModel?> GetInvoiceReconciliationAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        store.ReadAsync(database =>
        {
            var invoice = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            if (invoice is null) return null;
            var contract = ReconciliationContract(database, invoice);
            return new InvoiceReconciliationModel(invoice, contract,
                contract is null ? [] : ReconciliationPositions(database, contract),
                database.InvoicePaymentAllocations.Where(item => item.InvoiceId == invoiceId).ToList(),
                contract is null ? [] : database.ContractChanges.Where(item => item.ContractId == contract.Id && item.Kind == ContractChangeKind.Extension && item.IsConfirmed).OrderBy(item => item.AgreementDate).ToList(),
                database.InvoiceContractChangeLinks.SingleOrDefault(item => item.InvoiceId == invoiceId)?.ContractChangeId);
        }, cancellationToken);

    public Task<ReturnActionResult> AllocateInvoicePaymentAsync(Guid invoiceId, Guid positionId, BillingPositionSource source,
        decimal valueExVat, string? actor = null, CancellationToken cancellationToken = default, Guid? extensionId = null) =>
        store.UpdateAsync(database =>
        {
            var invoice = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            var contract = invoice is null ? null : ReconciliationContract(database, invoice);
            var position = contract is null ? null : ReconciliationPositions(database, contract)
                .SingleOrDefault(item => item.PositionId == positionId && item.Source == source);
            if (invoice is null || position is null || position.ValueExVat <= 0)
                return new ReturnActionResult(false, "Choose a committed payment position belonging to this invoice's contract.", []);
            var existingChangeLink = database.InvoiceContractChangeLinks.SingleOrDefault(item => item.InvoiceId == invoiceId);
            var selectedExtensionId = extensionId ?? existingChangeLink?.ContractChangeId;
            if (position.IsOptionalExtension &&
                (!database.ContractChanges.Any(item => item.Id == selectedExtensionId && item.ContractId == contract!.Id && item.Kind == ContractChangeKind.Extension && item.IsConfirmed)
                 || existingChangeLink is not null && existingChangeLink.ContractChangeId != selectedExtensionId))
                return new ReturnActionResult(false, "Select a confirmed extension for this contract. The invoice cannot be linked to a different agreement from its existing contract-change link.", []);
            if (valueExVat == 0 || decimal.Round(valueExVat, 2) != valueExVat || Math.Sign(valueExVat) != Math.Sign(invoice.TotalCostExVat))
                return new ReturnActionResult(false, "Enter an ex-VAT amount with at most two decimal places, positive for an invoice or negative for a credit note.", []);
            if (database.InvoicePaymentAllocations.Any(item => item.InvoiceId == invoiceId && item.PositionId == positionId && item.Source == source))
                return new ReturnActionResult(false, "This invoice already has an allocation to this position. Remove it before replacing it.", []);
            var allocated = database.InvoicePaymentAllocations.Where(item => item.InvoiceId == invoiceId).Sum(item => item.ValueExVat);
            if (Math.Abs(allocated + valueExVat) > Math.Abs(invoice.TotalCostExVat))
                return new ReturnActionResult(false, "The allocation exceeds this invoice's unallocated value.", []);
            if (position.AllocatedValueExVat + valueExVat < 0 || position.AllocatedValueExVat + valueExVat > position.ValueExVat)
                return new ReturnActionResult(false, "The allocation must leave the position's reconciled value between zero and its scheduled value.", []);
            var now = timeProvider.GetUtcNow();
            database.InvoicePaymentAllocations.Add(new(invoiceId, positionId, source, valueExVat, now));
            if (position.IsOptionalExtension && existingChangeLink is null)
            {
                database.InvoiceContractChangeLinks.Add(new(invoiceId, selectedExtensionId!.Value));
                RecordAudit(database, now, "InvoiceExtensionLinked", "Invoice", invoiceId,
                    $"Linked invoice {invoice.InvoiceNumber} to extension {selectedExtensionId:D} while reconciling payment position {positionId:D}.", null, actor);
            }
            RecordAudit(database, now, "InvoicePaymentAllocated", "Invoice", invoiceId,
                $"Allocated {valueExVat:N2} ex VAT from {invoice.InvoiceNumber} to {position.Description} ({positionId:D}).", null, actor);
            return new ReturnActionResult(true, "Payment allocation saved.", []);
        }, cancellationToken);

    public Task<ReturnActionResult> RemoveInvoicePaymentAllocationAsync(Guid invoiceId, Guid positionId, BillingPositionSource source,
        string? actor = null, CancellationToken cancellationToken = default) =>
        store.UpdateAsync(database =>
        {
            var allocation = database.InvoicePaymentAllocations.SingleOrDefault(item => item.InvoiceId == invoiceId && item.PositionId == positionId && item.Source == source);
            if (allocation is null) return new ReturnActionResult(false, "This allocation no longer exists.", []);
            var netAfterRemoval = database.InvoicePaymentAllocations.Where(item => item.PositionId == positionId && item.Source == source).Sum(item => item.ValueExVat) - allocation.ValueExVat;
            if (netAfterRemoval < 0)
                return new ReturnActionResult(false, "Remove the linked credit-note allocations first so the position's reconciled value does not become negative.", []);
            var invoice = database.Invoices.SingleOrDefault(item => item.Id == invoiceId);
            var contract = invoice is null ? null : ReconciliationContract(database, invoice);
            var position = contract is null ? null : ReconciliationPositions(database, contract).SingleOrDefault(item => item.PositionId == positionId && item.Source == source);
            if (position is not null && netAfterRemoval > position.ValueExVat)
                return new ReturnActionResult(false, "Remove the replacement invoice allocation first so the position does not exceed its scheduled value.", []);
            database.InvoicePaymentAllocations.Remove(allocation);
            RecordAudit(database, timeProvider.GetUtcNow(), "InvoicePaymentAllocationRemoved", "Invoice", invoiceId,
                $"Removed allocation of {allocation.ValueExVat:N2} ex VAT to payment position {positionId:D}.", null, actor);
            return new ReturnActionResult(true, "Payment allocation removed.", []);
        }, cancellationToken);
}
