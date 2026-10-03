using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web;
using Remi.Web.Components;
using Remi.Web.Components.Pages;
using Xunit;

namespace Remi.Tests;

public sealed partial class RegisterComponentTests
{
    [Fact]
    public async Task Every_home_fact_opens_the_exact_contract_or_position_count()
    {
        using var context = CreateContext(timeProvider: new PortfolioClock());
        var store = context.Services.GetRequiredService<IRemiStore>();
        await store.UpdateAsync(database =>
        {
            var contract = database.Contracts[0] with { EndDate = new(2026, 10, 3) };
            var optional = contract with { Id = Guid.NewGuid(), SupplierReference = "OPTIONS" };
            database.Contracts[0] = contract;
            database.Contracts.Add(optional);
            database.ChargeScheduleItems.AddRange([
                new(Guid.NewGuid(), contract.Id, null, 1, "September licence", new(2026, 9, 30), 100, false, DateTimeOffset.UtcNow),
                new(Guid.NewGuid(), contract.Id, null, 1, "October licence", new(2026, 10, 1), 200, false, DateTimeOffset.UtcNow),
                new(Guid.NewGuid(), optional.Id, null, 2, "Optional extension", null, 300, true, DateTimeOffset.UtcNow),
            ]);
            return true;
        });
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/home?horizon=30");
        var home = context.Render<Dashboard>();
        home.WaitForAssertion(() => Assert.Equal(5, home.FindAll(".home-facts dd > a").Count));
        var links = home.FindAll(".home-facts dd > a").Select(item => (Href: item.GetAttribute("href")!, Count: item.TextContent.Trim())).ToList();
        home.Dispose();
        foreach (var (href, count) in links)
        {
            navigation.NavigateTo(href);
            if (href.StartsWith("/contracts", StringComparison.Ordinal))
            {
                using var register = context.Render<Contracts>();
                register.WaitForAssertion(() => Assert.Equal(count, register.Find(".register-result-count strong").TextContent));
                Assert.Equal("30", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query)["horizon"].ToString());
            }
            else
            {
                using var planned = context.Render<Invoices>();
                planned.WaitForAssertion(() => Assert.Equal(count, planned.Find(".register-result-count strong").TextContent));
                Assert.Single(planned.FindAll(".planned-table tbody td:first-child a"));
            }
        }
    }

    [Fact]
    public async Task Contract_filters_sort_and_pagination_survive_URL_navigation()
    {
        using var context = CreateContext(additionalContracts: 14, timeProvider: new PortfolioClock());
        await context.Services.GetRequiredService<IRemiStore>().UpdateAsync(database =>
        {
            for (var index = 0; index < database.Contracts.Count; index++)
                database.Contracts[index] = database.Contracts[index] with { StartDate = new(2026, 1, 1) };
            return true;
        });
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var original = "/contracts?status=live&q=EXTRA&sort=reference&descending=false&page=2&horizon=90";
        navigation.NavigateTo(original);
        var cut = context.Render<Contracts>();
        cut.WaitForAssertion(() => Assert.Equal("EXTRA-011", cut.Find("tbody a.register-reference").TextContent));
        navigation.NavigateTo("/contracts?status=future");
        cut.WaitForAssertion(() => Assert.Equal("0", cut.Find(".register-result-count strong").TextContent));
        navigation.NavigateTo(original);
        cut.WaitForAssertion(() => Assert.Equal("EXTRA-011", cut.Find("tbody a.register-reference").TextContent));
        Assert.Equal("EXTRA", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public async Task Reporting_selection_changes_preparation_without_moving_billing_months_or_submitted_NIL_to_ready()
    {
        using var context = CreateContext(includeSubmittedReturn: true, timeProvider: new PortfolioClock());
        await context.Services.GetRequiredService<IRemiStore>().UpdateAsync(database =>
        {
            database.MonthlyReturns.Add(new(Guid.NewGuid(), FrameworkCode.GCloud14, "2026-07", ReturnStatus.CorrectionRequired, null, null, null, DateTimeOffset.UtcNow));
            return true;
        });
        var home = context.Render<Dashboard>();
        home.WaitForAssertion(() => Assert.Contains("Submitted · NIL", home.Find(".home-report-table").TextContent));
        Assert.Contains("Correction required", home.Find(".home-report-table").TextContent);
        var before = home.Find(".home-facts").TextContent;
        context.Services.GetRequiredService<ReportingPeriodContext>().Synchronise(["2026-07", "2026-08"], "2026-08");
        home.WaitForAssertion(() => Assert.Contains("August 2026", home.Find(".home-reporting h2").TextContent));
        Assert.Equal(before, home.Find(".home-facts").TextContent);
        Assert.Contains("No activity · review NIL", home.Find(".home-report-table").TextContent);
    }

    [Fact]
    public async Task Planned_view_shows_undated_optional_rows_for_review_but_excludes_them_from_monthly_forecasts()
    {
        using var context = CreateContext(includePaymentSchedule: true, timeProvider: new PortfolioClock());
        await context.Services.GetRequiredService<IRemiStore>().UpdateAsync(database =>
        {
            database.ChargeScheduleItems[1] = database.ChargeScheduleItems[1] with { ExpectedInvoiceDate = null };
            return true;
        });
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/invoices?view=planned&review=undated&month=2026-09");
        var cut = context.Render<Invoices>();
        cut.WaitForAssertion(() => Assert.Contains("Optional · agreement needed", cut.Find(".planned-table").TextContent));
        Assert.Equal("1", cut.Find(".register-result-count strong").TextContent);
        Assert.Contains("section=schedule", cut.Find(".planned-table a").GetAttribute("href"));
        navigation.NavigateTo("/invoices?view=planned&month=2026-09");
        cut.WaitForAssertion(() => Assert.Equal("0", cut.Find(".register-result-count strong").TextContent));
        navigation.NavigateTo("/invoices");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".invoice-register-table tbody tr")));
    }

    [Fact]
    public void Context_links_open_the_requested_section_and_return_to_the_filter()
    {
        using var context = CreateContext(timeProvider: new PortfolioClock());
        var returnTo = "/contracts?status=ending-options&horizon=90";
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(HomeRoutes.Contract(SampleContractId, "changes", returnTo));
        var cut = context.Render<ContractDetails>(parameters => parameters.Add(item => item.ContractId, SampleContractId));
        cut.WaitForAssertion(() => Assert.StartsWith("Changes", cut.Find(".contract-tabs button.active").TextContent));
        Assert.Equal(returnTo, cut.Find(".contract-hero-actions a").GetAttribute("href"));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("//example.com")]
    [InlineData("/contracts/../../settings")]
    [InlineData("/contracts\\example")]
    public void Context_return_links_cannot_leave_the_expected_register_routes(string path) => Assert.Null(HomeRoutes.SafeReturn(path));

    [Fact]
    public void Home_has_loading_error_and_retry_states_without_read_side_effects()
    {
        using var context = CreateContext(timeProvider: new PortfolioClock());
        var store = new DeferredHomeStore();
        context.Services.AddSingleton(new ReportingWorkspace(store, null!, null!, null!, null!, new PortfolioClock()));
        var home = context.Render<Dashboard>();
        Assert.Contains("Loading", home.Find("[role='status']").TextContent);
        store.Completion.SetException(new IOException("Read failed"));
        home.WaitForAssertion(() => Assert.Contains("Home could not be loaded", home.Find("[role='alert']").TextContent));
        store.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Completion.SetResult(new RemiDatabase());
        home.Find("button").Click();
        home.WaitForAssertion(() => Assert.Equal(5, home.FindAll(".home-facts > div").Count));
        Assert.Contains("No live contracts end", home.Markup);
        Assert.Contains("No committed positions have explicit dates", home.Markup);
    }

    private sealed class DeferredHomeStore : IRemiStore
    {
        public TaskCompletionSource<RemiDatabase> Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) => reader(await Completion.Task);
        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Reads must not write.");
    }
}
