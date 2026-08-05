using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web;
using Remi.Web.Components.Layout;
using ContractsRegister = Remi.Web.Components.Pages.Contracts;
using ReportingRegister = Remi.Web.Components.Pages.Reporting;
using Xunit;

namespace Remi.Tests;

public sealed class RegisterComponentTests
{
    [Fact]
    public void Header_carries_the_current_reporting_period_without_rendering_a_selector()
    {
        using var context = CreateContext();

        var cut = context.Render<MainLayout>();

        Assert.Equal("/?period=2026-07", cut.Find("a.brand").GetAttribute("href"));
        Assert.Equal("contracts?period=2026-07", cut.Find("nav a[href^='contracts']").GetAttribute("href"));
        Assert.Contains("Reports", cut.Find("nav").TextContent);
        Assert.DoesNotContain("Monthly return register", cut.Find("nav").TextContent);
        Assert.DoesNotContain("Templates & audit", cut.Find("nav").TextContent);
        Assert.Contains("Settings", cut.Find("nav").TextContent);
        Assert.DoesNotContain("Maintenance", cut.Find("nav").TextContent);
        Assert.Empty(cut.FindAll(".reporting-period-control"));
        Assert.DoesNotContain("Reporting period", cut.Find("header.app-header").TextContent);
        Assert.DoesNotContain("Procurement team", cut.Find("header.app-header").TextContent);
        Assert.Empty(cut.FindAll(".user-context, .user-avatar"));
    }

    [Fact]
    public void Contract_register_shows_selection_controls_only_when_a_record_is_selected()
    {
        using var context = CreateContext();

        var cut = context.Render<ContractsRegister>();

        cut.WaitForAssertion(() => Assert.Contains("Select contracts to review them together.", cut.Markup));
        Assert.DoesNotContain("Selected contracts", cut.Markup);

        cut.Find("input[aria-label='Select RM-001']").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Selected contracts", cut.Markup);
            Assert.Contains("1 selected", cut.Markup);
            Assert.Contains("Review selected", cut.Markup);
        });

        cut.Find("button.clear-selection").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Select contracts to review them together.", cut.Markup);
            Assert.DoesNotContain("Selected contracts", cut.Markup);
        });
    }

    [Fact]
    public void Contract_register_opens_a_record_when_its_row_is_activated_by_keyboard()
    {
        using var context = CreateContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<ContractsRegister>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.register-row-actionable")));
        var expectedRoute = cut.Find("a.register-reference").GetAttribute("href");

        cut.Find("tr.register-row-actionable").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.NotNull(expectedRoute);
        Assert.EndsWith(expectedRoute, navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Monthly_return_register_prepares_workbooks_without_accepting_completed_return_imports()
    {
        using var context = CreateContext();
        var cut = context.Render<ReportingRegister>();

        cut.WaitForAssertion(() => Assert.Contains("Open", cut.Markup));
        cut.FindAll("button").First(button => button.TextContent.Trim().StartsWith("Open", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Generate the reporting workbook", cut.Markup);
            Assert.Contains("Prepare MI workbook", cut.Markup);
            Assert.DoesNotContain("Monthly MI workbook", cut.Markup);
            Assert.Empty(cut.FindAll("input[type='file']"));
        });
    }

    private static BunitContext CreateContext()
    {
        var database = new RemiDatabase
        {
            Contracts =
            [
                new ContractRecord(
                    Guid.NewGuid(),
                    FrameworkCode.GCloud14,
                    "RM-001",
                    "Example customer",
                    "URN-001",
                    new DateOnly(2026, 1, 1),
                    new DateOnly(2026, 12, 31),
                    "Lot 1",
                    null,
                    null,
                    null,
                    null,
                    null,
                    1000,
                    "2026-07",
                    "test.xlsx",
                    DateTimeOffset.UtcNow),
            ],
        };
        var reportingPeriod = new ReportingPeriodContext(TimeProvider.System);
        reportingPeriod.Synchronise(["2026-07"], "2026-07");
        var context = new BunitContext();
        context.Services.AddSingleton(reportingPeriod);
        context.Services.AddSingleton(new ReportingWorkspace(new InMemoryStore(database), null!, null!, null!, null!, TimeProvider.System));
        return context;
    }

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) =>
            Task.FromResult(update(database));
    }
}
