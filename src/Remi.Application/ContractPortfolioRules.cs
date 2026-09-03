using Remi.Domain;

namespace Remi.Application;

public enum ContractLifecycle { Unknown, Future, Live, Ended }
public enum ContractDeliveryStatus { NotLive, PartiallyLive, Live }

public sealed record ContractCommercialPosition(
    DateOnly? EndDate,
    decimal AgreedChangeValueExVat,
    decimal ContractValueExVat,
    int UnconfirmedChangeCount);

/// <summary>Shared current-register rules; selecting an earlier report does not rewind agreements.</summary>
public static class ContractPortfolioRules
{
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public static ContractCommercialPosition CommercialPosition(
        ContractRecord contract, IReadOnlyList<ContractChangeRecord> changes)
    {
        // Recorded agreements already affect the register, including unconfirmed and future-dated
        // agreements. Retain that treatment, with confirmation exposed separately for review.
        var end = changes.Where(change => change.Kind == ContractChangeKind.Extension)
            .Select(change => change.EffectiveEndDate).Append(contract.EndDate).Max();
        var changeValue = changes.Sum(change => change.IncrementalValueExVat);
        return new(end, changeValue, contract.TotalContractValueExVat + changeValue,
            changes.Count(change => !change.IsConfirmed));
    }

    public static ContractLifecycle Lifecycle(DateOnly? start, DateOnly? end, DateOnly today)
    {
        if (start is null || end is null || end < start) return ContractLifecycle.Unknown;
        if (start > today) return ContractLifecycle.Future;
        return end < today ? ContractLifecycle.Ended : ContractLifecycle.Live;
    }

    public static bool IsEndingWithin(DateOnly? start, DateOnly? end, DateOnly today, int days) =>
        Lifecycle(start, end, today) == ContractLifecycle.Live && end!.Value.DayNumber - today.DayNumber <= days;

    public static ContractDeliveryStatus Delivery(IReadOnlyList<ContractServicePart> parts, DateOnly today)
    {
        var live = parts.Count(part => part.GoLiveDate is DateOnly date && date <= today);
        return live == 0 ? ContractDeliveryStatus.NotLive
            : live == parts.Count ? ContractDeliveryStatus.Live : ContractDeliveryStatus.PartiallyLive;
    }

    /// <summary>Prefer the charge schedule; use the legacy plan only when no charge schedule exists.</summary>
    public static decimal ScheduledBaseValue(
        IReadOnlyList<ChargeScheduleItem> schedule, IReadOnlyList<InvoicePlanItem> legacyPlan) =>
        schedule.Count > 0 ? schedule.Where(item => !item.IsOptionalExtension).Sum(item => item.ValueExVat)
            : legacyPlan.Sum(item => item.ExpectedValueExVat);

    public static decimal CommittedValue(ContractRecord contract, ContractCommercialPosition commercial,
        IReadOnlyList<ChargeScheduleItem> schedule, IReadOnlyList<InvoicePlanItem> legacyPlan)
    {
        var scheduled = ScheduledBaseValue(schedule, legacyPlan);
        return (scheduled > 0 ? scheduled : contract.TotalContractValueExVat) + commercial.AgreedChangeValueExVat;
    }

    // Advance billing and incomplete historical dates remain eligible. Only a known ended,
    // fully invoiced contract is omitted; eligibility must not be used as a count of live contracts.
    public static bool CanRegisterInvoice(ContractLifecycle lifecycle, decimal? remainingValue) =>
        lifecycle != ContractLifecycle.Ended || remainingValue is null or > 0;
}
