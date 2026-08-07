using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web;
using ContractRecordView = Remi.Web.Components.ContractRecordView;
using ContractRegistrationPage = Remi.Web.Components.Pages.ContractRegistration;
using Remi.Web.Components.Layout;
using ContractsRegister = Remi.Web.Components.Pages.Contracts;
using DashboardPage = Remi.Web.Components.Pages.Dashboard;
using InvoiceRecordView = Remi.Web.Components.InvoiceRecordView;
using InvoiceRegistrationPage = Remi.Web.Components.Pages.InvoiceRegistration;
using InvoicesRegister = Remi.Web.Components.Pages.Invoices;
using ReportingRegister = Remi.Web.Components.Pages.Reporting;
using TemplatesPage = Remi.Web.Components.Pages.Templates;
using Xunit;

namespace Remi.Tests;

public sealed class RegisterComponentTests
{
    private static readonly Guid SampleContractId = Guid.Parse("405b5dd4-0b92-4576-99a9-d2cc7851a2b5");
    private static readonly Guid SampleVasContractId = Guid.Parse("9f2dc10e-9554-47d0-8870-8dbb6bb94e4a");
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
            Assert.Equal(12, registration.FindAll(".invoice-details-grid label").Count);
        });
    }

    [Fact]
    public void G_cloud_invoice_form_cascades_the_service_group_from_the_selected_lot()
    {
        using var context = CreateContext();
        var registration = context.Render<InvoiceRegistrationPage>();

        registration.Find("input[role='combobox'][aria-label='Contract']").Input("RM-001");
        registration.WaitForAssertion(() => Assert.Single(registration.FindAll("button[role='option']")));
        registration.Find("button[role='option']").Click();

        registration.WaitForAssertion(() =>
        {
            var lot = registration.Find("select[aria-label='Lot number']");
            Assert.Equal(["", "1", "2", "3"], lot.QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToList());
            Assert.False(registration.Find("select[aria-label='Service group']").HasAttribute("disabled"));
            Assert.Equal("Information and Communication Technology (ICT)", registration.Find("select[aria-label='Service group']").GetAttribute("value"));
            Assert.Equal(["", "Per Unit", "Per User"], registration.Find("select[aria-label='Unit of measure']").QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToList());
        });

        registration.Find("select[aria-label='Lot number']").Change("3");

        registration.WaitForAssertion(() =>
        {
            var serviceGroup = registration.Find("select[aria-label='Service group']");
            Assert.False(serviceGroup.HasAttribute("disabled"));
            Assert.Equal(
                ["", "Ongoing Support", "Planning", "Security Services", "Setup and Migration", "Testing", "Training"],
                serviceGroup.QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToList());
        });
    }

    [Fact]
    public void G_cloud_contract_registration_uses_exact_template_fields_and_keeps_the_lot_cascade_together()
    {
        using var context = CreateContext();
        var registration = context.Render<ContractRegistrationPage>();

        var framework = registration.Find("select[aria-label='Framework']");
        Assert.Equal(["", "GCloud14", "VerticalApplicationSolutions"], framework.QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToList());
        framework.Change(FrameworkCode.GCloud14.ToString());

        registration.WaitForAssertion(() =>
        {
            var labels = registration.FindAll(".floating-label").Select(label => label.TextContent.Trim()).ToList();
            Assert.Contains("Supplier reference number", labels);
            Assert.Contains("Customer Unique Reference Number (URN)", labels);
            Assert.Contains("Customer organisation name", labels);
            Assert.Contains("Contract start date", labels);
            Assert.Contains("Contract end date", labels);
            Assert.Contains("Lot number", labels);
            Assert.Contains("Service Group", labels);
            Assert.Contains("Digital Marketplace Service ID", labels);
            Assert.Contains("Total contract value", labels);
            Assert.DoesNotContain("Product/Service Description", labels);
            Assert.DoesNotContain("Order Channel", labels);

            var serviceSection = registration.FindAll(".invoice-details-section").Single(section => section.QuerySelector("h2")?.TextContent.Trim() == "Service classification");
            Assert.Equal(
                ["Lot number", "Service Group", "Digital Marketplace Service ID"],
                serviceSection.QuerySelectorAll(".floating-label").Select(label => label.TextContent.Trim()).ToList());
            Assert.True(serviceSection.QuerySelector("select[aria-label='Service Group']")!.HasAttribute("disabled"));
            Assert.Equal("digital-marketplace-service-suggestions", serviceSection.QuerySelector("input[list]")!.GetAttribute("list"));
            Assert.Equal(
                ["115981361947474"],
                registration.FindAll("#digital-marketplace-service-suggestions option").Select(option => option.GetAttribute("value")).ToList());
        });

        registration.Find("select[aria-label='Lot number']").Change("2");

        registration.WaitForAssertion(() =>
        {
            var serviceGroup = registration.Find("select[aria-label='Service Group']");
            Assert.False(serviceGroup.HasAttribute("disabled"));
            Assert.Contains("Information and Communication Technology (ICT)", serviceGroup.QuerySelectorAll("option").Select(option => option.TextContent.Trim()));
        });
    }

    [Fact]
    public void Vas_contract_registration_uses_only_the_vas_template_fields_and_order_channel_lookup()
    {
        using var context = CreateContext();
        var registration = context.Render<ContractRegistrationPage>();

        registration.Find("select[aria-label='Framework']").Change(FrameworkCode.VerticalApplicationSolutions.ToString());

        registration.WaitForAssertion(() =>
        {
            var labels = registration.FindAll(".floating-label").Select(label => label.TextContent.Trim()).ToList();
            Assert.Contains("Supplier Reference Number", labels);
            Assert.Contains("Customer Organisation Name", labels);
            Assert.Contains("Customer Unique Reference Number (URN)", labels);
            Assert.Contains("Lot Number", labels);
            Assert.Contains("Product/Service Description", labels);
            Assert.Contains("Order Channel", labels);
            Assert.Contains("Contract Start Date", labels);
            Assert.Contains("Contract End Date", labels);
            Assert.Contains("Total Contract Value", labels);
            Assert.DoesNotContain("Service Group", labels);
            Assert.DoesNotContain("Digital Marketplace Service ID", labels);
            Assert.Equal(
                ["", "Direct Award", "Further Competition"],
                registration.Find("select[aria-label='Order Channel']").QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToList());
        });
    }

    [Fact]
    public void Vas_invoice_form_cascades_product_group_from_lot_and_uses_its_own_fields()
    {
        using var context = CreateContext(FrameworkCode.VerticalApplicationSolutions);
        var registration = context.Render<InvoiceRegistrationPage>();

        registration.Find("input[role='combobox'][aria-label='Contract']").Input("VAS-001");
        registration.WaitForAssertion(() => Assert.Single(registration.FindAll("button[role='option']")));
        registration.Find("button[role='option']").Click();

        registration.WaitForAssertion(() =>
        {
            Assert.Contains("Vertical Application Solutions invoice report fields", registration.Markup);
            Assert.Empty(registration.FindAll("select[aria-label='Unit of measure']"));
            Assert.DoesNotContain("Digital Marketplace service ID", registration.Markup);
        });

        registration.Find("select[aria-label='Lot number']").Change("3");

        registration.WaitForAssertion(() =>
        {
            var productGroup = registration.Find("select[aria-label='Product/service group level 1']");
            Assert.False(productGroup.HasAttribute("disabled"));
            Assert.Contains("Geographic Information System (GIS)", productGroup.TextContent);
            Assert.Equal(
                ["", "Software", "Hardware", "Associated Service"],
                registration.Find("select[aria-label='Product/service group level 2']").QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToList());
        });
    }

    [Fact]
    public void Contract_invoice_form_gives_vas_the_same_lot_to_product_group_assistance()
    {
        using var context = CreateContext(FrameworkCode.VerticalApplicationSolutions);
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(component => component.ContractId, SampleVasContractId));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".contract-tabs button")));
        cut.FindAll(".contract-tabs button").Single(button => button.TextContent.Trim().StartsWith("Invoices")).Click();
        cut.WaitForAssertion(() => Assert.Equal("Register", cut.Find(".contract-card-head button.primary").TextContent.Trim()));
        cut.Find(".contract-card-head button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Choose a lot before its dependent product or service group.", cut.Markup);
            Assert.False(cut.Find("select[aria-label='Product/service group level 1']").HasAttribute("disabled"));
        });

        cut.Find("select[aria-label='Lot number']").Change("3");

        cut.WaitForAssertion(() =>
        {
            var productGroup = cut.Find("select[aria-label='Product/service group level 1']");
            Assert.Contains("Geographic Information System (GIS)", productGroup.TextContent);
            Assert.Equal(
                ["", "Software", "Hardware", "Associated Service"],
                cut.Find("select[aria-label='Product/service group level 2']").QuerySelectorAll("option").Select(option => option.GetAttribute("value")).ToList());
        });
    }

    [Fact]
    public async Task Latest_invoice_values_are_suggested_for_the_next_invoice_contract_fields()
    {
        using var context = CreateContext();
        var workspace = context.Services.GetRequiredService<ReportingWorkspace>();
        var recorded = await workspace.RecordInvoiceAsync(new InvoiceEntry(
            FrameworkCode.GCloud14,
            "RM-001",
            "Invoice customer",
            "URN-INVOICE",
            new DateOnly(2026, 8, 1),
            "INV-LATEST",
            "3",
            "Planning",
            null,
            "Latest reporting service",
            null,
            "987654321",
            "Per User",
            4,
            125,
            500,
            "Latest vendor",
            "Latest subcontractor",
            "2026-08",
            "test"));

        Assert.True(recorded.Succeeded);

        var suggestion = await workspace.GetInvoiceReportingSuggestionAsync(SampleContractId);

        Assert.Equal("Invoice customer", suggestion.CustomerName);
        Assert.Equal("URN-INVOICE", suggestion.CustomerUrn);
        Assert.Equal("3", suggestion.LotNumber);
        Assert.Equal("Planning", suggestion.ServiceGroup);
        Assert.Equal("Latest reporting service", suggestion.ServiceDescription);
        Assert.Equal("987654321", suggestion.DigitalMarketplaceServiceId);
        Assert.Equal("Per User", suggestion.UnitOfMeasure);
        Assert.Equal(4, suggestion.Quantity);
        Assert.Equal("Latest vendor", suggestion.OriginalVendor);
        Assert.Equal("Latest subcontractor", suggestion.SubcontractorName);
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
            Assert.All(cut.FindAll(".dashboard-row-action"), action =>
                Assert.Matches("^/reports/\\d+/2026-07\\?period=2026-07$", action.GetAttribute("href")));
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

        cut.WaitForAssertion(() =>
        {
            var heading = cut.Find("header.contract-hero");
            Assert.Contains("dashboard-header", heading.ClassList);
            Assert.Contains("register-page-header-compact", heading.ClassList);
            Assert.Equal("RM-001", heading.QuerySelector("h1")!.TextContent.Trim());
            Assert.Equal(
                "Example customer · G-Cloud 14",
                heading.QuerySelector(".contract-hero-context")!.TextContent.Trim());
            Assert.Equal("Edit", cut.Find(".contract-hero-actions button.secondary").TextContent.Trim());
            Assert.Equal(3, cut.FindAll(".record-display-grid").Count);
            Assert.Empty(cut.FindAll(".contract-edit-panel"));
            var serviceSection = cut.FindAll(".contract-detail-section").Single(section => section.QuerySelector("h3")?.TextContent.Contains("Service classification") == true);
            Assert.Equal(
                ["Lot number", "Service Group", "Digital Marketplace Service ID"],
                serviceSection.QuerySelectorAll("dt").Select(term => term.TextContent.Trim()).ToList());
            Assert.DoesNotContain("Service group / level 2", cut.Markup);
            Assert.DoesNotContain("Service description", cut.Markup);
            Assert.DoesNotContain("Order channel", cut.Markup);
        });
        cut.Find(".contract-hero-actions button.secondary").Click();

        cut.WaitForAssertion(() =>
        {
            var actions = cut.Find(".contract-hero-actions");
            Assert.Equal(["Save", "Cancel"], actions.QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
            Assert.Empty(actions.QuerySelectorAll("a"));
            Assert.False(actions.QuerySelector("button.primary")!.HasAttribute("disabled"));
            Assert.Empty(cut.FindAll(".record-display-grid"));
            Assert.Equal(11, cut.FindAll(".contract-edit-panel .floating-field").Count);
            Assert.Equal("digital-marketplace-service-suggestions", cut.Find(".contract-edit-panel input[list]").GetAttribute("list"));
            Assert.Equal("115981361947474", cut.Find("#digital-marketplace-service-suggestions option").GetAttribute("value"));
        });
    }

    [Fact]
    public void Vas_contract_view_shows_only_vas_template_fields()
    {
        using var context = CreateContext(FrameworkCode.VerticalApplicationSolutions);
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(component => component.ContractId, SampleVasContractId));

        cut.WaitForAssertion(() =>
        {
            var serviceSection = cut.FindAll(".contract-detail-section").Single(section => section.QuerySelector("h3")?.TextContent.Contains("Service classification") == true);
            Assert.Equal(
                ["Lot Number", "Product/Service Description", "Order Channel"],
                serviceSection.QuerySelectorAll("dt").Select(term => term.TextContent.Trim()).ToList());
            Assert.DoesNotContain("Service Group", serviceSection.TextContent);
            Assert.DoesNotContain("Digital Marketplace Service ID", serviceSection.TextContent);
        });
    }

    [Fact]
    public void Saving_a_contract_invoice_closes_the_form_and_starts_the_total_blank()
    {
        using var context = CreateContext();
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(component => component.ContractId, SampleContractId));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".contract-tabs button")));
        cut.FindAll(".contract-tabs button").Single(button => button.TextContent.Trim().StartsWith("Invoices")).Click();
        cut.WaitForAssertion(() => Assert.Equal("Register", cut.Find(".contract-card-head button.primary").TextContent.Trim()));
        cut.Find(".contract-card-head button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            var form = cut.Find(".contract-invoice-form");
            Assert.Null(form.QuerySelector("input[type='number']")!.GetAttribute("value"));
            Assert.NotNull(form.QuerySelector(".contract-invoice-prefill .invoice-field-help"));
        });

        var invoiceForm = cut.Find(".contract-invoice-form");
        invoiceForm.QuerySelector("input[placeholder=' ']")!.Input("INV-NEW");
        invoiceForm.QuerySelector("input[type='date']")!.Change("2026-07-15");
        invoiceForm.QuerySelector("input[type='number']")!.Change("250");

        cut.WaitForAssertion(() => Assert.False(cut.Find(".contract-invoice-actions .invoice-command-save").HasAttribute("disabled")));
        cut.Find(".contract-invoice-actions .invoice-command-save").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".contract-invoice-form")));
    }

    [Fact]
    public void Invoice_editing_replaces_display_fields_with_matching_floating_fields()
    {
        using var context = CreateContext();
        var cut = context.Render<InvoiceRecordView>(parameters => parameters.Add(component => component.InvoiceId, SampleInvoiceId));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".record-display-grid").Count);
            Assert.Empty(cut.FindAll(".contract-edit-panel"));
        });

        cut.Find(".contract-hero-actions button.secondary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".record-display-grid"));
            Assert.Equal(19, cut.FindAll(".contract-edit-panel .floating-field").Count);
            Assert.Equal(["Save", "Cancel"], cut.Find(".contract-hero-actions").QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
        });
    }

    [Fact]
    public void Invoice_register_uses_the_designation_as_its_only_record_opening_control()
    {
        using var context = CreateContext();
        var cut = context.Render<InvoicesRegister>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".invoice-register-table tbody tr")));
        var row = cut.Find(".invoice-register-table tbody tr");

        Assert.Equal("Invoice designation ↓", cut.FindAll(".invoice-register-table th")[1].TextContent.Trim());
        Assert.Null(row.GetAttribute("tabindex"));
        Assert.Single(row.QuerySelectorAll("a.register-reference"));
        Assert.Empty(row.QuerySelectorAll(".table-action-cell"));
        Assert.Empty(row.QuerySelectorAll(".remi-action"));
        Assert.Equal("Check ↕", cut.FindAll(".invoice-register-table th")[6].TextContent.Trim());
        Assert.DoesNotContain("checks passed", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checks passed", row.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RM6259", row.TextContent);
        Assert.DoesNotContain("excl. VAT", row.TextContent);
    }

    [Fact]
    public void Monthly_return_register_prepares_workbooks_without_accepting_completed_return_imports()
    {
        using var context = CreateContext();
        var cut = context.Render<ReportingRegister>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("a[href^='/reports/']")));
        Assert.DoesNotContain("Generate workbook", cut.Markup);

        var workspace = context.Render<ReportingRegister>(parameters => parameters
            .Add(component => component.FrameworkValue, (int)FrameworkCode.GCloud14)
            .Add(component => component.WorkspaceMonth, "2026-07"));

        workspace.WaitForAssertion(() =>
        {
            Assert.Contains("Generate workbook", workspace.Markup);
            Assert.Contains("Review data", workspace.Markup);
            Assert.Contains("Generated files", workspace.Markup);
            Assert.DoesNotContain("Monthly MI workbook", workspace.Markup);
            Assert.Empty(workspace.FindAll("input[type='file']"));
        });
    }

    [Fact]
    public void Reports_register_separates_contracts_invoices_and_submission_from_lifecycle_status()
    {
        using var context = CreateContext();
        var reports = context.Render<ReportingRegister>();

        reports.WaitForAssertion(() =>
        {
            var table = reports.Find(".return-register-table table");
            Assert.Contains("Contracts", table.TextContent);
            Assert.Contains("Invoices", table.TextContent);
            Assert.Contains("Submission", table.TextContent);
            Assert.DoesNotContain("Activity", table.TextContent);
            Assert.DoesNotContain("Readiness", table.TextContent);
        });
    }

    [Fact]
    public void Return_workspace_reloads_its_framework_when_a_different_open_link_is_followed()
    {
        using var context = CreateContext();
        var workspace = context.Render<ReportingRegister>(parameters => parameters
            .Add(component => component.FrameworkValue, (int)FrameworkCode.GCloud13)
            .Add(component => component.WorkspaceMonth, "2026-07"));

        workspace.WaitForAssertion(() => Assert.Equal("G-Cloud 13", workspace.Find("h1").TextContent.Trim()));

        workspace.Render(parameters => parameters
            .Add(component => component.FrameworkValue, (int)FrameworkCode.GCloud14)
            .Add(component => component.WorkspaceMonth, "2026-07"));

        workspace.WaitForAssertion(() =>
        {
            Assert.Equal("G-Cloud 14", workspace.Find("h1").TextContent.Trim());
        });
    }

    [Fact]
    public void Return_workspace_shows_a_gca_summary_and_invoice_purchase_order_number()
    {
        using var context = CreateContext();
        var workspace = context.Render<ReportingRegister>(parameters => parameters
            .Add(component => component.FrameworkValue, (int)FrameworkCode.VerticalApplicationSolutions)
            .Add(component => component.WorkspaceMonth, "2026-07"));

        workspace.WaitForAssertion(() =>
        {
            var summary = workspace.Find(".gca-return-summary");
            Assert.Contains("RM6259 reporting summary", summary.TextContent);
            Assert.Contains("Invoices", summary.TextContent);
            Assert.Contains("Purchase order", summary.TextContent);
            Assert.Contains("GCA_VAS_202607", summary.TextContent);
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

    private static BunitContext CreateContext(FrameworkCode? additionalFramework = null)
    {
        var database = new RemiDatabase
        {
            DigitalMarketplaceServices = [new DigitalMarketplaceService("115981361947474", "StatMap Cluster")],
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
                    "2",
                    "Information and Communication Technology (ICT)",
                    null,
                    null,
                    null,
                    "123456",
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
                    "3",
                    "Geographic Information System (GIS)",
                    "Software",
                    "StatMap GIS system",
                    null,
                    null,
                    "Per unit",
                    1,
                    500,
                    500,
                    InvoiceReportingDefaults.OriginalVendor,
                    InvoiceReportingDefaults.SubcontractorName,
                    "2026-07",
                    "RM6259 source.xlsx",
                    DateTimeOffset.UtcNow),
            ],
        };
        if (additionalFramework == FrameworkCode.VerticalApplicationSolutions)
        {
            database.Contracts.Add(new ContractRecord(
                SampleVasContractId,
                FrameworkCode.VerticalApplicationSolutions,
                "VAS-001",
                "VAS example customer",
                "URN-002",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 12, 31),
                "2",
                null,
                null,
                "Example VAS service",
                "Direct Award",
                null,
                1000,
                "2026-07",
                "test.xlsx",
                DateTimeOffset.UtcNow));
        }
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
