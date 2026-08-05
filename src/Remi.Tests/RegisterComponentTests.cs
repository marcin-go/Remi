using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web;
using ContractRecordView = Remi.Web.Components.ContractRecordView;
using Remi.Web.Components.Layout;
using ContractsRegister = Remi.Web.Components.Pages.Contracts;
using DashboardPage = Remi.Web.Components.Pages.Dashboard;
using InvoiceRegistrationPage = Remi.Web.Components.Pages.InvoiceRegistration;
using InvoicesRegister = Remi.Web.Components.Pages.Invoices;
using ReportingRegister = Remi.Web.Components.Pages.Reporting;
using TemplatesPage = Remi.Web.Components.Pages.Templates;
using Xunit;

namespace Remi.Tests;

public sealed class RegisterComponentTests
{
    private static readonly Guid SampleContractId = Guid.Parse("405b5dd4-0b92-4576-99a9-d2cc7851a2b5");
    private static readonly Guid SampleInvoiceId = Guid.Parse("d461989e-a1e8-4450-a371-31f7f1028df1");

    [Fact]
    public void Header_carries_the_current_reporting_period_without_rendering_a_selector()
    {
        using var context = CreateContext();

        var cut = context.Render<MainLayout>();

        Assert.Equal("/home?period=2026-07", cut.Find("a.brand").GetAttribute("href"));
        Assert.Equal("Remi home", cut.Find("a.brand").GetAttribute("aria-label"));
        Assert.Equal("/contracts?period=2026-07", cut.Find("nav a[href^='/contracts']").GetAttribute("href"));
        Assert.Equal("/reports?period=2026-07", cut.Find("nav a[href^='/reports']").GetAttribute("href"));
        Assert.Equal("/settings?period=2026-07", cut.Find("nav a[href^='/settings']").GetAttribute("href"));
        Assert.Contains("Home", cut.Find("nav").TextContent);
        Assert.DoesNotContain("Dashboard", cut.Find("nav").TextContent);
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
            Assert.Contains("Review", cut.Markup);
        });

        cut.Find("button.clear-selection").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Select contracts to review them together.", cut.Markup);
            Assert.DoesNotContain("Selected contracts", cut.Markup);
        });
    }

    [Fact]
    public void Register_and_reports_headings_omit_redundant_summaries()
    {
        using var context = CreateContext();

        var contracts = context.Render<ContractsRegister>();
        contracts.WaitForAssertion(() =>
        {
            var heading = contracts.Find("header.dashboard-header");
            Assert.Null(heading.QuerySelector(".eyebrow"));
            Assert.DoesNotContain("Live register", heading.TextContent);
            Assert.Empty(contracts.FindAll(".register-period-summary"));
        });

        var invoices = context.Render<InvoicesRegister>();
        invoices.WaitForAssertion(() =>
        {
            var heading = invoices.Find("header.dashboard-header");
            Assert.Null(heading.QuerySelector(".eyebrow"));
            Assert.DoesNotContain("Live register", heading.TextContent);
            Assert.Empty(invoices.FindAll(".register-period-summary"));
        });

        var reports = context.Render<ReportingRegister>();
        reports.WaitForAssertion(() =>
        {
            Assert.Empty(reports.FindAll(".register-period-summary"));
            var heading = reports.Find(".return-register-heading");
            Assert.Null(heading.QuerySelector(".eyebrow"));
            Assert.Equal("Browse reports", heading.QuerySelector("h2")?.TextContent.Trim());
        });
    }

    [Fact]
    public void Invoice_register_links_to_a_standalone_invoice_registration_page()
    {
        using var context = CreateContext();

        var invoices = context.Render<InvoicesRegister>();
        invoices.WaitForAssertion(() =>
            Assert.Equal("/invoices/new?period=2026-07", invoices.Find("a.remi-action--primary").GetAttribute("href")));

        var registration = context.Render<InvoiceRegistrationPage>();
        registration.WaitForAssertion(() => Assert.Equal("Register invoice", registration.Find("h1").TextContent.Trim()));
        Assert.Empty(registration.FindAll("select[aria-label='Contract']"));
        Assert.Contains("Choose a contract and enter the invoice details.", registration.Markup);
        Assert.Empty(registration.FindAll(".register-breadcrumbs"));
        Assert.DoesNotContain("Step 1 of 2", registration.Markup);
        Assert.DoesNotContain("Step 2 of 2", registration.Markup);
        Assert.DoesNotContain("Register contract", registration.Markup);

        registration.Find("input[role='combobox'][aria-label='Contract']").Input("RM-001");
        registration.WaitForAssertion(() => Assert.Single(registration.FindAll("button[role='option']")));
        registration.Find("button[role='option']").Click();
        registration.WaitForAssertion(() =>
        {
            Assert.Contains("RM-001", registration.Markup);
            Assert.Single(registration.FindAll(".invoice-contract-summary"));
            Assert.Single(registration.FindAll(".invoice-intake-actions"));
            Assert.Equal(4, registration.FindAll(".invoice-details-grid label").Count);
        });
    }

    [Fact]
    public void Dashboard_uses_the_defined_information_and_navigation_hierarchy()
    {
        using var context = CreateContext();

        var cut = context.Render<DashboardPage>();

        cut.WaitForAssertion(() =>
        {
            var dashboardHeader = cut.Find(".dashboard-header");
            Assert.Null(dashboardHeader.QuerySelector(".eyebrow"));
            Assert.Equal("Prepare →", dashboardHeader.QuerySelector("a.remi-action--primary")?.TextContent.Trim());

            var readinessHeader = cut.Find(".dashboard-readiness .dashboard-section-heading");
            Assert.Equal("Return readiness", readinessHeader.QuerySelector("h2")?.TextContent.Trim());
            Assert.Contains("Frameworks included in the July 2026 reporting period.", readinessHeader.TextContent);

            var attentionHeader = cut.Find(".dashboard-attention .dashboard-section-heading");
            Assert.Equal("Needs attention", attentionHeader.QuerySelector("h2")?.TextContent.Trim());

            var activityHeader = cut.Find(".dashboard-activity .dashboard-section-heading");
            Assert.Equal("Recent activity", activityHeader.QuerySelector("h2")?.TextContent.Trim());
            Assert.Equal("View →", activityHeader.QuerySelector("a.remi-action--section")?.TextContent.Trim());

            var tableHeaders = cut.FindAll(".dashboard-table th").Select(header => header.TextContent.Trim()).ToList();
            Assert.Equal(["Framework", "Contracts", "Invoices", "Readiness", "Action"], tableHeaders);
            Assert.All(cut.FindAll(".dashboard-table td.table-action-cell"), cell =>
                Assert.Contains("→", cell.TextContent));
        });
    }

    [Fact]
    public void Contract_register_uses_the_designation_as_its_only_record_opening_control()
    {
        using var context = CreateContext();
        var cut = context.Render<ContractsRegister>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".contract-register-table tbody tr")));
        var row = cut.Find(".contract-register-table tbody tr");

        Assert.Null(row.GetAttribute("tabindex"));
        Assert.Single(row.QuerySelectorAll("a.register-reference"));
        Assert.Empty(row.QuerySelectorAll(".table-action-cell"));
        Assert.Empty(row.QuerySelectorAll(".remi-action"));
        Assert.DoesNotContain("Lot", row.TextContent);
        Assert.DoesNotContain("excl. VAT", row.TextContent);
    }

    [Fact]
    public void Contract_editing_replaces_hero_actions_with_save_and_cancel()
    {
        using var context = CreateContext();
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(component => component.ContractId, SampleContractId));

        cut.WaitForAssertion(() => Assert.Equal("Edit", cut.Find(".contract-hero-actions button.secondary").TextContent.Trim()));
        cut.Find(".contract-hero-actions button.secondary").Click();

        cut.WaitForAssertion(() =>
        {
            var actions = cut.Find(".contract-hero-actions");
            Assert.Equal(["Save", "Cancel"], actions.QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
            Assert.Empty(actions.QuerySelectorAll("a"));
            Assert.False(actions.QuerySelector("button.primary")!.HasAttribute("disabled"));
        });
    }

    [Fact]
    public void Invoice_register_uses_the_designation_as_its_only_record_opening_control()
    {
        using var context = CreateContext();
        var cut = context.Render<InvoicesRegister>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".invoice-register-table tbody tr")));
        var row = cut.Find(".invoice-register-table tbody tr");

        Assert.StartsWith("Invoice", cut.FindAll(".invoice-register-table th")[1].TextContent.Trim(), StringComparison.Ordinal);
        Assert.Null(row.GetAttribute("tabindex"));
        Assert.Single(row.QuerySelectorAll("a.register-reference"));
        Assert.Empty(row.QuerySelectorAll(".table-action-cell"));
        Assert.Empty(row.QuerySelectorAll(".remi-action"));
        Assert.DoesNotContain("RM6259", row.TextContent);
        Assert.DoesNotContain("excl. VAT", row.TextContent);
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
            Assert.Contains("Generate", cut.Find(".return-outcome-panel .action-row").TextContent);
            Assert.Contains("Prepare", cut.Markup);
            Assert.DoesNotContain("Monthly MI workbook", cut.Markup);
            Assert.Empty(cut.FindAll("input[type='file']"));
        });
    }

    [Fact]
    public void Template_settings_stages_a_workbook_before_explicit_registration()
    {
        using var context = CreateContext();
        var cut = context.Render<TemplatesPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Drop approved workbook here", cut.Markup);
            Assert.Contains("Register a GCA approved workbook", cut.Markup);
            Assert.Contains("REGISTER", cut.Markup);
            Assert.Empty(cut.FindAll(".template-file-selection"));
            Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
            Assert.Single(cut.FindAll("input[type='file']"));
            Assert.DoesNotContain("Generate a review copy", cut.Markup);
            Assert.DoesNotContain("Template version", cut.Markup);
            Assert.DoesNotContain("Review notes", cut.Markup);
            Assert.DoesNotContain("Official guidance", cut.Markup);
            Assert.DoesNotContain("Settings ", cut.Markup);
        });
    }

    private static BunitContext CreateContext()
    {
        var database = new RemiDatabase
        {
            Contracts =
            [
                new ContractRecord(
                    SampleContractId,
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
            Invoices =
            [
                new InvoiceRecord(
                    SampleInvoiceId,
                    FrameworkCode.VerticalApplicationSolutions,
                    "RM-001",
                    "Example customer",
                    "URN-001",
                    new DateOnly(2026, 7, 1),
                    "INV-001",
                    "Lot 1",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    500,
                    null,
                    null,
                    "2026-07",
                    "RM6259 source.xlsx",
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
