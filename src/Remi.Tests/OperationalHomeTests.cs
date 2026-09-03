using System.Text.Json;
using Remi.Application;
using Remi.Domain;
using Remi.Web;
using Xunit;

namespace Remi.Tests;

public sealed class OperationalHomeTests
{
    private static readonly DateOnly Today = new(2026, 9, 3);
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, 0, ContractLifecycle.Live)]
    [InlineData(0, 0, ContractLifecycle.Live)]
    [InlineData(0, 1, ContractLifecycle.Live)]
    [InlineData(1, 30, ContractLifecycle.Future)]
    [InlineData(-30, -1, ContractLifecycle.Ended)]
    [InlineData(1, 0, ContractLifecycle.Unknown)]
    public void Lifecycle_uses_valid_inclusive_dates(int start, int end, ContractLifecycle expected) =>
        Assert.Equal(expected, ContractPortfolioRules.Lifecycle(Today.AddDays(start), Today.AddDays(end), Today));

    [Fact]
    public void Missing_dates_are_unknown()
    {
        Assert.Equal(ContractLifecycle.Unknown, ContractPortfolioRules.Lifecycle(null, Today, Today));
        Assert.Equal(ContractLifecycle.Unknown, ContractPortfolioRules.Lifecycle(Today, null, Today));
        Assert.Equal(ContractLifecycle.Unknown, ContractPortfolioRules.Lifecycle(null, null, Today));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task Ending_filters_include_today_and_the_horizon_but_exclude_future_invalid_and_ended_contracts(int horizon)
    {
        var database = new RemiDatabase
        {
            Contracts =
            [
                Contract("TODAY", Today), Contract("BOUNDARY", Today.AddDays(horizon)),
                Contract("LATER", Today.AddDays(horizon + 1)), Contract("ENDED", Today.AddDays(-1)),
                Contract("FUTURE", Today.AddDays(horizon)) with { StartDate = Today.AddDays(1) },
                Contract("INVALID", Today.AddDays(-1)) with { StartDate = Today },
                Contract("UNKNOWN", null),
            ],
        };
        var home = await Home(database).GetAsync(horizon);
        Assert.Equal(horizon, home.EndingWithinDays);
        Assert.Equal(["TODAY", "BOUNDARY"], home.MatchingContracts(PortfolioFilter.Ending).Select(item => item.Contract.SupplierReference));
        Assert.Equal(3, home.MatchingContracts(PortfolioFilter.Live).Count);
        Assert.Single(home.MatchingContracts(PortfolioFilter.Future));
        Assert.Single(home.MatchingContracts(PortfolioFilter.Ended));
        Assert.Equal(2, home.MatchingContracts(PortfolioFilter.UnknownDates).Count);
    }

    [Fact]
    public async Task Unsupported_horizon_is_rejected_before_reading()
    {
        var store = new ReadOnlyStore(new());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new OperationalHomeWorkspace(store, new FixedClock(Now)).GetAsync(60));
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task Extension_rules_preserve_all_recorded_agreements_and_keep_ambiguous_options_out_of_decision_counts()
    {
        var contract = Contract("EXTENDED", Today.AddDays(-1));
        var noOptions = Contract("NO-OPTIONS", Today.AddDays(10));
        var unused = Contract("UNUSED", Today.AddDays(20));
        var database = new RemiDatabase
        {
            Contracts = [contract, noOptions, unused],
            ContractChanges =
            [
                Extension(contract.Id, Today.AddDays(30), 300),
                Extension(contract.Id, Today.AddDays(90), 200) with { IsConfirmed = false, AgreementDate = Today.AddDays(7) },
                Extension(contract.Id, Today.AddDays(500), -50) with { Kind = ContractChangeKind.Variation },
            ],
            ChargeScheduleItems =
            [
                Position(contract.Id, 2, 300, Today, optional: true),
                Position(contract.Id, 2, 200, Today, optional: true),
                Position(contract.Id, 3, 400, Today.AddYears(1), optional: true),
                Position(unused.Id, 2, 200, null, optional: true),
                Position(unused.Id, 2, 300, null, optional: true),
            ],
        };
        var home = await Home(database).GetAsync();
        var row = home.Contracts.Single(item => item.Contract.Id == contract.Id);
        Assert.Equal(Today.AddDays(90), row.Commercial.EndDate);
        Assert.Equal(1450, row.Commercial.ContractValueExVat);
        Assert.Equal(1450, row.CommittedValueExVat);
        Assert.Equal(1, row.Commercial.UnconfirmedChangeCount);
        Assert.Equal(ContractLifecycle.Live, row.Lifecycle);
        Assert.Equal([2, 3], row.RecordedOptionYears);
        Assert.Equal(RecordedOptionState.AssociationNeedsReview, row.OptionState);
        Assert.Equal(2, row.ExtensionsNeedingAssociation.Count);
        Assert.Equal(contract.Id, Assert.Single(home.MatchingContracts(PortfolioFilter.EndingOptionsNeedReview)).Contract.Id);
        Assert.Equal(unused.Id, Assert.Single(home.MatchingContracts(PortfolioFilter.EndingOptionsRecorded)).Contract.Id);
        Assert.Equal(noOptions.Id, Assert.Single(home.MatchingContracts(PortfolioFilter.EndingNoOptionRecorded)).Contract.Id);
        Assert.Empty(home.ThisMonth.Positions);
    }

    [Fact]
    public async Task Payment_months_include_final_billing_and_expose_incomplete_dates_without_counting_options_or_duplicate_legacy_rows()
    {
        var ended = Contract("FINAL-BILL", Today.AddDays(-1));
        var undated = Contract("UNDATED", Today.AddYears(1));
        var missing = Contract("NO-SCHEDULE", Today.AddYears(1));
        var database = new RemiDatabase
        {
            Contracts = [ended, undated, missing],
            ChargeScheduleItems =
            [
                Position(ended.Id, 1, 100, Today), Position(ended.Id, 1, 200, Today.AddDays(1)),
                Position(ended.Id, 2, 700, Today, optional: true),
                Position(ended.Id, 1, 300, new DateOnly(2026, 10, 1)),
                Position(undated.Id, 1, 400, null), Position(undated.Id, 2, 500, null, optional: true),
            ],
            InvoicePlanItems = [new(Guid.NewGuid(), ended.Id, "Overlapping legacy row", Today, 999)],
        };
        var home = await Home(database).GetAsync();
        Assert.Equal(2, home.ThisMonth.PositionCount);
        Assert.Equal(300, home.ThisMonth.ValueExVat);
        Assert.Equal(1, home.NextMonth.PositionCount);
        Assert.Equal(300, home.NextMonth.ValueExVat);
        Assert.Equal(6, home.PaymentPositions.Count);
        Assert.All(home.PaymentPositions, item => Assert.Equal(BillingPositionSource.ChargeSchedule, item.Source));
        Assert.Equal(1, home.FullyDatedSchedules);
        Assert.Equal(2, home.UndatedPositionCount);
        Assert.Equal(1, home.ContractsWithoutSchedule);
        Assert.Equal(600, home.Contracts.Single(item => item.Contract.Id == ended.Id).CommittedValueExVat);
        Assert.All(home.ThisMonth.Positions, item => Assert.Equal(ended.Id, item.ContractId));
    }

    [Fact]
    public async Task Legacy_plan_is_used_only_when_no_charge_schedule_exists_and_zero_complete_schedules_remain_visible()
    {
        var legacy = Contract("LEGACY", Today);
        var current = Contract("CURRENT", Today);
        var database = new RemiDatabase
        {
            Contracts = [legacy, current],
            ChargeScheduleItems = [Position(current.Id, 2, 500, null, optional: true)],
            InvoicePlanItems =
            [
                new(Guid.NewGuid(), legacy.Id, "Legacy dated", Today, 120),
                new(Guid.NewGuid(), legacy.Id, "Legacy undated", null, 180),
                new(Guid.NewGuid(), current.Id, "Superseded", Today, 999),
            ],
        };
        var home = await Home(database).GetAsync();
        Assert.Equal(BillingPositionSource.LegacyPlan, Assert.Single(home.ThisMonth.Positions).Source);
        Assert.Equal(0, home.FullyDatedSchedules);
        Assert.Equal(2, home.UndatedPositionCount);
        Assert.Equal(0, home.ContractsWithoutSchedule);
        Assert.Equal(300, home.Contracts.Single(item => item.Contract.Id == legacy.Id).CommittedValueExVat);
        Assert.Equal(1000, home.Contracts.Single(item => item.Contract.Id == current.Id).CommittedValueExVat);
    }

    [Fact]
    public async Task Invoice_totals_include_credits_use_framework_and_normalised_reference_and_flag_ambiguous_contracts()
    {
        var contract = Contract("REF", Today);
        var otherFramework = contract with { Id = Guid.NewGuid(), Framework = FrameworkCode.GCloud13 };
        var duplicate = Contract("DUP", Today);
        var database = new RemiDatabase
        {
            Contracts = [contract, otherFramework, duplicate, duplicate with { Id = Guid.NewGuid(), SupplierReference = " dup " }],
            Invoices =
            [
                Invoice(contract, 600) with { SupplierReference = " ref " }, Invoice(contract, -100),
                Invoice(otherFramework, 100), Invoice(duplicate, 250),
                Invoice(contract, 999) with { SupplierReference = "UNMATCHED" },
            ],
        };
        var home = await Home(database).GetAsync();
        Assert.Equal(500, home.Contracts.Single(item => item.Contract.Id == contract.Id).RemainingCommittedValueExVat);
        Assert.Equal(900, home.Contracts.Single(item => item.Contract.Id == otherFramework.Id).RemainingCommittedValueExVat);
        Assert.Equal(2, home.AmbiguousInvoiceMatches);
        Assert.All(home.Contracts.Where(item => item.HasAmbiguousInvoiceMatch), item => Assert.Null(item.RemainingCommittedValueExVat));
        Assert.Equal(4, home.MatchingContracts(PortfolioFilter.Live).Count);
    }

    [Fact]
    public async Task Go_live_dates_are_inclusive_and_independent_of_the_commercial_term()
    {
        var contract = Contract("PARTS", Today.AddYears(1));
        var database = new RemiDatabase
        {
            Contracts = [contract],
            ContractServiceParts =
            [
                new(Guid.NewGuid(), contract.Id, "Live today", Today, 0, Now),
                new(Guid.NewGuid(), contract.Id, "Future", Today.AddDays(1), 1, Now),
                new(Guid.NewGuid(), contract.Id, "Unknown", null, 2, Now),
            ],
        };
        var row = Assert.Single((await Home(database).GetAsync()).Contracts);
        Assert.Equal(ContractLifecycle.Live, row.Lifecycle);
        Assert.Equal(ContractDeliveryStatus.PartiallyLive, row.Delivery);
        Assert.Equal(ContractDeliveryStatus.NotLive, ContractPortfolioRules.Delivery([], Today));
        Assert.Equal(ContractDeliveryStatus.NotLive, ContractPortfolioRules.Delivery([database.ContractServiceParts[1]], Today));
        Assert.Equal(ContractDeliveryStatus.Live, ContractPortfolioRules.Delivery([database.ContractServiceParts[0]], Today));
    }

    [Theory]
    [InlineData(2026, 12, 31, 2027, 1, 1)]
    [InlineData(2028, 2, 28, 2028, 2, 29)]
    public async Task Local_business_date_controls_lifecycle_and_planning_across_year_and_leap_day_boundaries(
        int year, int month, int day, int localYear, int localMonth, int localDay)
    {
        var clock = new FixedClock(new(year, month, day, 23, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.CreateCustomTimeZone("Test UTC+1", TimeSpan.FromHours(1), "Test UTC+1", "Test UTC+1"));
        var today = new DateOnly(localYear, localMonth, localDay);
        var contract = Contract("LOCAL", today) with { StartDate = today };
        var database = new RemiDatabase
        {
            Contracts = [contract],
            ChargeScheduleItems = [Position(contract.Id, 1, 100, today)],
        };
        var home = await new OperationalHomeWorkspace(new ReadOnlyStore(database), clock).GetAsync();
        Assert.Equal(today, home.AsAtDate);
        Assert.Equal(ContractLifecycle.Live, Assert.Single(home.Contracts).Lifecycle);
        Assert.Equal(new DateOnly(localYear, localMonth, 1), home.ThisMonth.Month);
        Assert.Equal(home.ThisMonth.Month.AddMonths(1), home.NextMonth.Month);
        Assert.Single(home.ThisMonth.Positions);
        Assert.True(ContractPortfolioRules.IsEndingWithin(today, today.AddDays(30), today, 30));
    }

    [Fact]
    public async Task Operational_snapshot_reads_once_does_not_write_and_is_independent_of_reporting_selection()
    {
        var contract = Contract("READ-ONLY", Today);
        var database = new RemiDatabase
        {
            Contracts = [contract],
            ChargeScheduleItems = [Position(contract.Id, 1, 100, Today)],
            MonthlyReturns = [new(Guid.NewGuid(), contract.Framework, "2026-08", ReturnStatus.NilReturn, Now, "submitted", null, Now)],
        };
        var before = JsonSerializer.Serialize(database);
        var store = new ReadOnlyStore(database);
        var clock = new FixedClock(Now);
        var reporting = Reporting(store, clock);
        var home = new OperationalHomeWorkspace(store, clock);
        var first = await home.GetAsync();
        Assert.Equal(1, store.ReadCount);
        var period = new ReportingPeriodContext(clock);
        period.Synchronise(["2026-08", "2025-01"], "2025-01");
        await reporting.GetDashboardAsync(period.SelectedPeriod);
        var second = await home.GetAsync();
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(new DateOnly(2026, 9, 1), second.ThisMonth.Month);
        Assert.Equal(new DateOnly(2026, 10, 1), second.NextMonth.Month);
        var returns = await reporting.GetMonthlyReturnRegisterAsync();
        var submitted = returns.Entries.Single(item => item.Framework.Code == contract.Framework && item.ReportingMonth == "2026-08");
        Assert.Equal(ReportLifecycleStatus.Submitted, submitted.LifecycleStatus);
        Assert.True(submitted.IsNilReturn);
        Assert.Equal(before, JsonSerializer.Serialize(database));
    }

    [Fact]
    public async Task Shared_projection_keeps_extension_dates_values_and_confirmation_consistent_with_details_and_invoice_selection()
    {
        var contract = Contract("EXTENDED", Today.AddDays(-30));
        var extension = Extension(contract.Id, Today.AddDays(90), 500) with { IsConfirmed = false };
        var database = new RemiDatabase
        {
            Contracts = [contract], ContractChanges = [extension],
            // Fully invoiced but still in the extended term: the old base-end-date picker omitted it.
            Invoices = [Invoice(contract, 1500)],
        };
        var store = new ReadOnlyStore(database);
        var clock = new FixedClock(Now);
        var reporting = Reporting(store, clock);
        var operational = Assert.Single((await new OperationalHomeWorkspace(store, clock).GetAsync()).Contracts);
        var progress = Assert.Single((await reporting.GetDashboardAsync("2026-08")).ContractProgress);
        var detail = Assert.IsType<ContractDetailsModel>(await reporting.GetContractDetailsAsync(contract.Id));
        var eligible = Assert.Single(await reporting.GetInvoiceRegistrationContractsAsync());
        Assert.Equal(1500, operational.CommittedValueExVat);
        Assert.Equal(operational.CommittedValueExVat, progress.ComparisonValueExVat);
        Assert.Equal(operational.CommittedValueExVat, detail.CommittedValueExVat);
        Assert.Equal(operational.Commercial.ContractValueExVat, progress.TotalContractValueExVat);
        Assert.Equal(operational.Commercial.EndDate, progress.EndDate);
        Assert.Equal(1, progress.UnconfirmedChangeCount);
        Assert.Equal(1, eligible.UnconfirmedChangeCount);
        Assert.Equal(0, eligible.RemainingCommittedValueExVat);
        Assert.Contains(detail.Findings, finding => finding.Code == "ContractChangeUnconfirmed");
    }

    [Fact]
    public async Task Invoice_selection_keeps_ended_final_bills_and_advance_billing_without_labelling_them_live()
    {
        var ended = Contract("FINAL", Today.AddDays(-1));
        var paid = Contract("PAID", Today.AddDays(-1));
        var future = Contract("ADVANCE", Today.AddYears(1)) with { StartDate = Today.AddDays(1) };
        var database = new RemiDatabase
        {
            Contracts = [ended, paid, future], Invoices = [Invoice(ended, 900), Invoice(paid, 1000)],
        };
        var reporting = Reporting(new ReadOnlyStore(database), new FixedClock(Now));
        Assert.Equal(["ADVANCE", "FINAL"], (await reporting.GetInvoiceRegistrationContractsAsync()).Select(item => item.SupplierReference));
        Assert.Empty((await Home(database).GetAsync()).MatchingContracts(PortfolioFilter.Live));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scheduled_base_value_and_changes_agree_in_every_projection(bool useChargeSchedule)
    {
        var contract = Contract("SCHEDULED", Today);
        var database = new RemiDatabase
        {
            Contracts = [contract], ContractChanges = [Extension(contract.Id, Today.AddYears(1), 500)],
            Invoices = [Invoice(contract, 100)],
            ChargeScheduleItems = useChargeSchedule ? [Position(contract.Id, 1, 1200, Today)] : [],
            InvoicePlanItems = [new(Guid.NewGuid(), contract.Id, "Legacy", Today, useChargeSchedule ? 9999 : 1200)],
        };
        var reporting = Reporting(new ReadOnlyStore(database), new FixedClock(Now));
        var home = Assert.Single((await Home(database).GetAsync()).Contracts);
        var progress = Assert.Single((await reporting.GetDashboardAsync()).ContractProgress);
        var detail = Assert.IsType<ContractDetailsModel>(await reporting.GetContractDetailsAsync(contract.Id));
        var picker = Assert.Single(await reporting.GetInvoiceRegistrationContractsAsync());
        Assert.Equal(1700, home.CommittedValueExVat);
        Assert.Equal(1700, progress.ComparisonValueExVat);
        Assert.Equal(1700, detail.CommittedValueExVat);
        Assert.Equal(1600, picker.RemainingCommittedValueExVat);
        Assert.Equal(1500, home.Commercial.ContractValueExVat);
        Assert.Equal(1500, progress.TotalContractValueExVat);
    }

    private static OperationalHomeWorkspace Home(RemiDatabase database) => new(new ReadOnlyStore(database), new FixedClock(Now));
    private static ReportingWorkspace Reporting(IRemiStore store, TimeProvider clock) => new(store, null!, null!, null!, null!, clock);
    private static ContractRecord Contract(string reference, DateOnly? end) => new(Guid.NewGuid(), FrameworkCode.GCloud14,
        reference, "Example customer", "10000001", new DateOnly(2026, 1, 1), end, "2", "Cloud Software", null, null, null,
        "123456", 1000, "2026-01", "test.xlsx", Now);
    private static ContractChangeRecord Extension(Guid contractId, DateOnly end, decimal value) => new(Guid.NewGuid(), contractId,
        ContractChangeKind.Extension, Today.AddDays(-10), Today, end, value, true, true, "Agreement", Now);
    private static ChargeScheduleItem Position(Guid contractId, int year, decimal value, DateOnly? date, bool optional = false) =>
        new(Guid.NewGuid(), contractId, null, year, "Payment position", date, value, optional, Now);
    private static InvoiceRecord Invoice(ContractRecord contract, decimal value) => new(Guid.NewGuid(), contract.Framework,
        contract.SupplierReference, contract.CustomerName, contract.CustomerUrn, Today, Guid.NewGuid().ToString(), "2", "Cloud Software",
        null, null, null, "123456", "Per unit", 1, value, value, null, null, "2026-09", "test.xlsx", Now);

    private sealed class FixedClock(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
    }

    private sealed class ReadOnlyStore(RemiDatabase database) : IRemiStore
    {
        public int ReadCount { get; private set; }
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(reader(database));
        }
        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Operational reads must not write to the register.");
    }
}
