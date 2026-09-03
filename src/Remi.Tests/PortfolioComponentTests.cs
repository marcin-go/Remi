using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web.Components;
using Remi.Web.Components.Pages;
using Xunit;

namespace Remi.Tests;

public sealed partial class RegisterComponentTests
{
    private sealed class PortfolioClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task Register_live_filter_excludes_future_and_invalid_dates_and_matches_the_operational_snapshot()
    {
        var clock = new PortfolioClock();
        using var context = CreateContext(timeProvider: clock);
        var store = context.Services.GetRequiredService<IRemiStore>();
        await store.UpdateAsync(database =>
        {
            var contract = database.Contracts[0];
            database.Contracts.Add(contract with { Id = Guid.NewGuid(), SupplierReference = "FUTURE", StartDate = new(2026, 9, 4) });
            database.Contracts.Add(contract with { Id = Guid.NewGuid(), SupplierReference = "INVALID", EndDate = new(2025, 12, 31) });
            database.Contracts.Add(contract with { Id = Guid.NewGuid(), SupplierReference = "UNKNOWN", EndDate = null });
            return true;
        });
        var cut = context.Render<Contracts>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".contract-register-table tbody tr").Count));
        Assert.Single(cut.FindAll(".register-status").Where(item => item.TextContent == "Live"));
        Assert.Single(cut.FindAll(".register-status").Where(item => item.TextContent == "Not started"));
        Assert.Equal(2, cut.FindAll(".register-status").Count(item => item.TextContent == "Dates need review"));
        cut.Find("button[aria-label='Contract status']").Click();
        cut.FindAll("#contract-status-filter-options [role='option']").Single(item => item.TextContent.Trim() == "Live").Click();
        var home = await new OperationalHomeWorkspace(store, clock).GetAsync();
        var references = home.MatchingContracts(PortfolioFilter.Live).Select(item => item.Contract.SupplierReference).ToList();
        Assert.Equal(references, cut.FindAll(".contract-register-table tbody a.register-reference").Select(item => item.TextContent).ToList());
    }

    [Fact]
    public async Task Detail_does_not_treat_a_future_service_date_as_already_live()
    {
        using var context = CreateContext(timeProvider: new PortfolioClock());
        await context.Services.GetRequiredService<IRemiStore>().UpdateAsync(database =>
        {
            database.ContractServiceParts.Add(new(Guid.NewGuid(), SampleContractId, "Future delivery", new(2026, 9, 4), 0, DateTimeOffset.UtcNow));
            return true;
        });
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(item => item.ContractId, SampleContractId));
        cut.WaitForAssertion(() => Assert.Equal("Operational delivery: Not live", cut.Find(".contract-hero .contract-status").TextContent.Trim()));
        var register = context.Render<Contracts>();
        register.WaitForAssertion(() => Assert.Equal("Live", register.Find(".register-status").TextContent.Trim()));
    }

    [Theory]
    [InlineData(true, "Not started")]
    [InlineData(false, "Dates need review")]
    public async Task Detail_and_register_agree_on_future_and_invalid_terms(bool future, string expected)
    {
        using var context = CreateContext(timeProvider: new PortfolioClock());
        await context.Services.GetRequiredService<IRemiStore>().UpdateAsync(database =>
        {
            database.Contracts[0] = future ? database.Contracts[0] with { StartDate = new(2026, 9, 4) }
                : database.Contracts[0] with { EndDate = new(2025, 12, 31) };
            return true;
        });
        var detail = context.Render<ContractRecordView>(parameters => parameters.Add(item => item.ContractId, SampleContractId));
        detail.WaitForAssertion(() => Assert.Equal($"Operational delivery: {expected}", detail.Find(".contract-hero .contract-status").TextContent.Trim()));
        var register = context.Render<Contracts>();
        register.WaitForAssertion(() => Assert.Equal(expected, register.Find(".register-status").TextContent.Trim()));
    }
}
