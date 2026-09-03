using Remi.Domain;

namespace Remi.Application;

/// <summary>No state implies an option has been exercised or conclusively exhausted.</summary>
public enum RecordedOptionState { NoOptionRecorded, OptionsRecorded, AssociationNeedsReview }
public enum PortfolioFilter { All, Live, Future, UnknownDates, Ended, Ending, EndingNoOptionRecorded, EndingOptionsRecorded, EndingOptionsNeedReview, NoSchedule, ExtensionReview, EndedValue, UndatedSchedule }
public enum BillingPositionSource { ChargeSchedule, LegacyPlan }

public sealed record OperationalContract(
    ContractRecord Contract,
    ContractCommercialPosition Commercial,
    ContractLifecycle Lifecycle,
    ContractDeliveryStatus Delivery,
    RecordedOptionState OptionState,
    IReadOnlyList<int> RecordedOptionYears,
    IReadOnlyList<ContractChangeRecord> ExtensionsNeedingAssociation,
    decimal CommittedValueExVat,
    decimal? RegisteredInvoiceValueExVat,
    bool HasSchedule,
    int PositionCount,
    int UndatedPositionCount)
{
    public decimal? RemainingCommittedValueExVat => RegisteredInvoiceValueExVat is decimal invoiced
        ? Math.Max(0, CommittedValueExVat - invoiced) : null;
    public bool HasAmbiguousInvoiceMatch => RegisteredInvoiceValueExVat is null;

    public bool Matches(PortfolioFilter filter, DateOnly today, int endingWithinDays = 180) => filter switch
    {
        PortfolioFilter.All => true,
        PortfolioFilter.Live => Lifecycle == ContractLifecycle.Live,
        PortfolioFilter.Future => Lifecycle == ContractLifecycle.Future,
        PortfolioFilter.UnknownDates => Lifecycle == ContractLifecycle.Unknown,
        PortfolioFilter.Ended => Lifecycle == ContractLifecycle.Ended,
        PortfolioFilter.NoSchedule => !HasSchedule,
        PortfolioFilter.ExtensionReview => ExtensionsNeedingAssociation.Count > 0,
        PortfolioFilter.EndedValue => Lifecycle == ContractLifecycle.Ended && RemainingCommittedValueExVat > 0,
        PortfolioFilter.UndatedSchedule => UndatedPositionCount > 0,
        _ => ContractPortfolioRules.IsEndingWithin(Contract.StartDate, Commercial.EndDate, today, endingWithinDays)
            && (filter == PortfolioFilter.Ending
                || filter == PortfolioFilter.EndingNoOptionRecorded && OptionState == RecordedOptionState.NoOptionRecorded
                || filter == PortfolioFilter.EndingOptionsRecorded && OptionState == RecordedOptionState.OptionsRecorded
                || filter == PortfolioFilter.EndingOptionsNeedReview && OptionState == RecordedOptionState.AssociationNeedsReview),
    };
}

/// <summary>A known payment position, never a promised invoice count or an assertion of non-issue.</summary>
public sealed record ScheduledPaymentPosition(
    Guid PositionId, Guid ContractId, string SupplierReference, string CustomerName,
    string Description, DateOnly? ExpectedInvoiceDate, decimal ValueExVat, bool IsOptionalExtension,
    BillingPositionSource Source);

public sealed record ScheduledPaymentMonth(DateOnly Month, IReadOnlyList<ScheduledPaymentPosition> Positions)
{
    public int PositionCount => Positions.Count;
    public decimal ValueExVat => Positions.Sum(item => item.ValueExVat);
}

public sealed record OperationalHomeModel(
    DateOnly AsAtDate,
    int EndingWithinDays,
    IReadOnlyList<OperationalContract> Contracts,
    IReadOnlyList<ScheduledPaymentPosition> PaymentPositions,
    ScheduledPaymentMonth ThisMonth,
    ScheduledPaymentMonth NextMonth)
{
    // These lists are also the underlying drill-down results. UI counts must use the same lists.
    public IReadOnlyList<OperationalContract> MatchingContracts(PortfolioFilter filter) => Contracts
        .Where(item => item.Matches(filter, AsAtDate, EndingWithinDays))
        .OrderBy(item => item.Commercial.EndDate)
        .ThenBy(item => item.Contract.SupplierReference, StringComparer.OrdinalIgnoreCase).ToList();

    public int ContractsWithoutSchedule => Contracts.Count(item => !item.HasSchedule);
    public int FullyDatedSchedules => Contracts.Count(item => item.HasSchedule && item.UndatedPositionCount == 0);
    public int UndatedPositionCount => PaymentPositions.Count(item => item.ExpectedInvoiceDate is null);
    public int ContractsNeedingExtensionAssociation => Contracts.Count(item => item.ExtensionsNeedingAssociation.Count > 0);
    public int AmbiguousInvoiceMatches => Contracts.Count(item => item.HasAmbiguousInvoiceMatch);

    public IReadOnlyList<ScheduledPaymentPosition> MatchingPositions(DateOnly month, string? review = null) => PaymentPositions
        .Where(item => review switch
        {
            "undated" => item.ExpectedInvoiceDate is null,
            "overdue" => !item.IsOptionalExtension && item.ExpectedInvoiceDate < AsAtDate,
            _ => !item.IsOptionalExtension && item.ExpectedInvoiceDate is DateOnly date && date.Year == month.Year && date.Month == month.Month,
        })
        .OrderBy(item => item.ExpectedInvoiceDate)
        .ThenBy(item => item.SupplierReference, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record HomeDashboardModel(
    OperationalHomeModel Portfolio,
    string ReportingMonth,
    IReadOnlyList<MonthlyReturnRegisterEntry> Reports,
    IReadOnlyList<AuditEventSummary> RecentActivity)
{
    public int SubmittedCount => Reports.Count(item => item.LifecycleStatus == ReportLifecycleStatus.Submitted);
    public int BlockedCount => Reports.Count(item => item.LifecycleStatus != ReportLifecycleStatus.Submitted && item.BlockingFindingCount > 0);
    public int ReadyToReviewCount => Reports.Count(item => item.LifecycleStatus == ReportLifecycleStatus.Draft
        && item.BlockingFindingCount == 0 && item.ContractCount + item.InvoiceCount > 0);
    public DateOnly? EarliestUnfinishedDeadline => Reports.Where(item => item.LifecycleStatus != ReportLifecycleStatus.Submitted)
        .Select(item => item.Framework.ReportingDeadline?.Calculate(ReportingMonth)).Min();
}
