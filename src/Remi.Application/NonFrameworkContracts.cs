using Remi.Domain;

namespace Remi.Application;

public sealed record NonFrameworkContractEntry(string SupplierReference, string CustomerName, string? CustomerUrn,
    DateOnly? StartDate, DateOnly? EndDate, NonFrameworkContractTerms Terms);

public sealed partial class ReportingWorkspace
{
    public Task<ReturnActionResult> SaveNonFrameworkContractAsync(NonFrameworkContractEntry entry, Guid? contractId = null,
        string? actor = null, CancellationToken cancellationToken = default) => store.UpdateAsync(database =>
    {
        var existing = contractId is { } id ? database.Contracts.SingleOrDefault(item => item.Id == id) : null;
        if (contractId is not null && existing?.Framework != FrameworkCode.NonFramework)
            return new ReturnActionResult(false, "Select an existing non-framework contract.", []);
        var now = timeProvider.GetUtcNow();
        var record = new ContractRecord(existing?.Id ?? Guid.NewGuid(), FrameworkCode.NonFramework, entry.SupplierReference.Trim(),
            entry.CustomerName.Trim(), NullIfWhiteSpace(entry.CustomerUrn), entry.StartDate, entry.EndDate,
            null, null, null, null, null, null, entry.Terms.InitialAnnualChargeExVat, "", "Non-framework agreement", existing?.CreatedAtUtc ?? now,
            entry.Terms with { AgreementNotes = entry.Terms.AgreementNotes.Trim(), CpiReference = entry.Terms.CpiReference.Trim() });
        if (string.IsNullOrWhiteSpace(record.SupplierReference) || string.IsNullOrWhiteSpace(record.CustomerName))
            return new ReturnActionResult(false, "Enter a contract reference and customer name.", []);
        if (NonFrameworkContractRules.Validate(record) is { } error) return new ReturnActionResult(false, error, []);
        if (record.NonFrameworkTerms!.NoticeReceivedDate > Today()) return new ReturnActionResult(false, "The notice received date cannot be in the future.", []);
        if (database.Contracts.Any(item => item.Id != record.Id && item.Framework == record.Framework &&
            ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(record.SupplierReference)))
            return new ReturnActionResult(false, "That non-framework contract reference is already registered.", []);
        if (existing is not null)
        {
            var hasInvoices = database.Invoices.Any(item => item.Framework == existing.Framework &&
                ReportingRules.NormaliseReference(item.SupplierReference) == ReportingRules.NormaliseReference(existing.SupplierReference));
            var reviews = database.NonFrameworkAnnualReviews.Where(item => item.ContractId == record.Id).ToList();
            var positions = database.ChargeScheduleItems.Where(item => item.ContractId == record.Id).ToList();
            var hasAllocations = database.InvoicePaymentAllocations.Any(item => positions.Any(position => position.Id == item.PositionId));
            if (hasInvoices && ReportingRules.NormaliseReference(existing.SupplierReference) != ReportingRules.NormaliseReference(record.SupplierReference))
                return new ReturnActionResult(false, "The reference cannot change while invoices are linked to this agreement.", []);
            if ((hasInvoices || hasAllocations || reviews.Count > 0) && (existing.StartDate != record.StartDate || existing.NonFrameworkTerms!.InitialAnnualChargeExVat != record.NonFrameworkTerms.InitialAnnualChargeExVat))
                return new ReturnActionResult(false, "The original start date and annual charge cannot change after invoices, allocations or annual reviews have been recorded.", []);
            if (NonFrameworkContractRules.EffectiveEnd(record) is { } end && database.InvoicePaymentAllocations
                .Where(item => positions.Any(position => position.Id == item.PositionId && position.ExpectedInvoiceDate >= end))
                .GroupBy(item => (item.PositionId, item.Source)).Any(group => group.Sum(item => item.ValueExVat) != 0))
                return new ReturnActionResult(false, "Reconcile or credit allocations for annual periods starting on or after termination before recording the notice.", []);
            database.Contracts[database.Contracts.IndexOf(existing)] = record;
            var initial = positions.SingleOrDefault(item => item.ContractYear == 1 && item.Description == "Annual contract charge");
            if (initial is not null && !hasAllocations)
                database.ChargeScheduleItems[database.ChargeScheduleItems.IndexOf(initial)] = initial with { ValueExVat = record.NonFrameworkTerms.InitialAnnualChargeExVat, ExpectedInvoiceDate = record.StartDate };
        }
        else
        {
            database.Contracts.Add(record);
            EnsureDefaultContractServicePart(database, record, now);
            database.ChargeScheduleItems.Add(new(Guid.NewGuid(), record.Id, null, 1, "Annual contract charge", record.StartDate,
                entry.Terms.InitialAnnualChargeExVat, false, now));
        }
        RecordAudit(database, now, existing is null ? "NonFrameworkContractCreated" : "NonFrameworkContractUpdated", "Contract", record.Id,
            $"{(existing is null ? "Registered" : "Updated")} non-framework agreement {record.SupplierReference}: {entry.Terms.AgreementBasis}, {entry.Terms.TermKind}, initial annual charge {entry.Terms.InitialAnnualChargeExVat:N2} ex VAT.",
            existing is null ? entry.Terms.AgreementNotes : $"Previous terms: {existing.NonFrameworkTerms}. Updated terms: {record.NonFrameworkTerms}.", actor);
        return new ReturnActionResult(true, "Non-framework agreement saved. It is excluded from framework reports.", [], record.Id);
    }, cancellationToken);

    public Task<ReturnActionResult> RecordNonFrameworkAnnualReviewAsync(Guid contractId, int contractYear, decimal cpiPercentage,
        string cpiReference, string? actor = null, CancellationToken cancellationToken = default) => store.UpdateAsync(database =>
    {
        var contract = database.Contracts.SingleOrDefault(item => item.Id == contractId);
        if (contract?.NonFrameworkTerms is not { } terms || contract.Framework != FrameworkCode.NonFramework || contract.StartDate is not { } start)
            return new ReturnActionResult(false, "Select a non-framework agreement with a start date.", []);
        var reviews = database.NonFrameworkAnnualReviews.Where(item => item.ContractId == contractId).OrderBy(item => item.ContractYear).ToList();
        var nextYear = reviews.Count == 0 ? 2 : reviews[^1].ContractYear + 1;
        if (contractYear != nextYear) return new ReturnActionResult(false, $"Review contract year {nextYear} next. Each annual calculation must use the previous reviewed charge.", []);
        if (contractYear > NonFrameworkContractRules.ContractYear(contract, Today()) + 1)
            return new ReturnActionResult(false, "Only the current or next annual period can be reviewed.", []);
        if (start.Year + contractYear - 1 > 9999) return new ReturnActionResult(false, "The anniversary is outside the supported date range.", []);
        var effective = start.AddYears(contractYear - 1);
        if (NonFrameworkContractRules.EffectiveEnd(contract) is { } end && effective >= end)
            return new ReturnActionResult(false, "That annual period starts on or after the contract ends or terminates.", []);
        if (!terms.UsesCpi && cpiPercentage != 0) return new ReturnActionResult(false, "This agreement has no CPI uplift. Use a zero rate.", []);
        if (terms.UsesCpi && string.IsNullOrWhiteSpace(cpiReference)) return new ReturnActionResult(false, "Record the actual CPI source and reference period used for this review.", []);
        var previous = reviews.Count == 0 ? terms.InitialAnnualChargeExVat : reviews[^1].AnnualChargeExVat;
        decimal charge;
        try
        {
            if (cpiPercentage + terms.AdditionalPercentage <= -100) return new ReturnActionResult(false, "The combined uplift must leave a positive annual charge.", []);
            charge = NonFrameworkContractRules.CalculateUplift(previous, cpiPercentage, terms.AdditionalPercentage);
        }
        catch (OverflowException) { return new ReturnActionResult(false, "The uplift is too large to calculate.", []); }
        if (charge <= 0) return new ReturnActionResult(false, "The calculated annual charge must be positive.", []);
        var now = timeProvider.GetUtcNow();
        var review = new NonFrameworkAnnualReview(Guid.NewGuid(), contractId, contractYear, effective, previous, cpiPercentage,
            terms.AdditionalPercentage, charge, terms.UsesCpi ? cpiReference.Trim() : "No annual uplift", now);
        database.NonFrameworkAnnualReviews.Add(review);
        database.ChargeScheduleItems.Add(new(Guid.NewGuid(), contractId, null, contractYear, "Annual contract charge", effective, charge, false, now));
        RecordAudit(database, now, "NonFrameworkAnnualReviewRecorded", "Contract", contractId,
            $"Reviewed year {contractYear} from {effective:yyyy-MM-dd}: {previous:N2} × (1 + ({cpiPercentage}% CPI + {terms.AdditionalPercentage}%) / 100) = {charge:N2} ex VAT.", review.CpiReference, actor);
        return new ReturnActionResult(true, "Annual charge reviewed and its payment position recorded.", [], review.Id);
    }, cancellationToken);
}
