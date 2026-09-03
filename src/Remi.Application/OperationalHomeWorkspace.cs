using Remi.Domain;

namespace Remi.Application;

/// <summary>
/// An operational snapshot independent of reporting readiness. Reads once, uses grouped lookups,
/// and never loads evidence files, changes source rows or guesses invoice/option associations.
/// </summary>
public sealed class OperationalHomeWorkspace(IRemiStore store, TimeProvider timeProvider)
{
    public Task<OperationalHomeModel> GetAsync(int endingWithinDays = 180, CancellationToken cancellationToken = default)
    {
        if (endingWithinDays is not (30 or 90 or 180))
            throw new ArgumentOutOfRangeException(nameof(endingWithinDays), "Choose 30, 90 or 180 days.");
        var today = ContractPortfolioRules.Today(timeProvider);
        return store.ReadAsync(database => Build(database, today, endingWithinDays), cancellationToken);
    }

    private static OperationalHomeModel Build(RemiDatabase database, DateOnly today, int horizon)
    {
        var changes = database.ContractChanges.ToLookup(item => item.ContractId);
        var schedules = database.ChargeScheduleItems.ToLookup(item => item.ContractId);
        var legacyPlans = database.InvoicePlanItems.ToLookup(item => item.ContractId);
        var parts = database.ContractServiceParts.ToLookup(item => item.ContractId);
        var invoices = database.Invoices.ToLookup(item => Key(item.Framework, item.SupplierReference));
        var contractsByKey = database.Contracts.ToLookup(item => Key(item.Framework, item.SupplierReference));
        var positions = new List<ScheduledPaymentPosition>();
        var contracts = new List<OperationalContract>();

        foreach (var contract in database.Contracts)
        {
            var contractChanges = changes[contract.Id].ToList();
            var schedule = schedules[contract.Id].ToList();
            var legacy = legacyPlans[contract.Id].ToList();
            var commercial = ContractPortfolioRules.CommercialPosition(contract, contractChanges);
            var extensions = contractChanges.Where(item => item.Kind == ContractChangeKind.Extension).ToList();
            var optionYears = schedule.Where(item => item.IsOptionalExtension)
                .Select(item => item.ContractYear).Distinct().Order().ToList();
            // The schema cannot prove which period an extension exercised, including partial and
            // successive extensions. Do not manufacture a second extension opportunity from dates.
            var optionState = optionYears.Count == 0 ? RecordedOptionState.NoOptionRecorded
                : extensions.Count > 0 ? RecordedOptionState.AssociationNeedsReview : RecordedOptionState.OptionsRecorded;
            var contractPositions = schedule.Count > 0
                ? schedule.Select(item => new ScheduledPaymentPosition(item.Id, contract.Id, contract.SupplierReference,
                    contract.CustomerName, item.Description, item.ExpectedInvoiceDate, item.ValueExVat,
                    item.IsOptionalExtension, BillingPositionSource.ChargeSchedule)).ToList()
                : legacy.Select(item => new ScheduledPaymentPosition(item.Id, contract.Id, contract.SupplierReference,
                    contract.CustomerName, item.Label, item.ExpectedInvoiceDate, item.ExpectedValueExVat,
                    false, BillingPositionSource.LegacyPlan)).ToList();
            positions.AddRange(contractPositions);
            var key = Key(contract.Framework, contract.SupplierReference);
            contracts.Add(new OperationalContract(contract, commercial,
                ContractPortfolioRules.Lifecycle(contract.StartDate, commercial.EndDate, today),
                ContractPortfolioRules.Delivery(parts[contract.Id].ToList(), today), optionState, optionYears,
                extensions, ContractPortfolioRules.CommittedValue(contract, commercial, schedule, legacy),
                contractsByKey[key].Count() == 1 ? invoices[key].Sum(item => item.TotalCostExVat) : null,
                contractPositions.Count > 0, contractPositions.Count,
                contractPositions.Count(item => item.ExpectedInvoiceDate is null)));
        }

        var month = new DateOnly(today.Year, today.Month, 1);
        // Final bills on ended contracts remain visible. Optional rows are retained for review but
        // cannot enter committed forecasts until the user explicitly associates their agreement.
        ScheduledPaymentMonth Month(DateOnly start) => new(start, positions
            .Where(item => !item.IsOptionalExtension && item.ExpectedInvoiceDate is DateOnly date
                && date.Year == start.Year && date.Month == start.Month)
            .OrderBy(item => item.ExpectedInvoiceDate)
            .ThenBy(item => item.SupplierReference, StringComparer.OrdinalIgnoreCase).ToList());
        return new(today, horizon, contracts, positions, Month(month), Month(month.AddMonths(1)));
    }

    private static (FrameworkCode, string) Key(FrameworkCode framework, string reference) =>
        (framework, ReportingRules.NormaliseReference(reference));
}
