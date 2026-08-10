using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web;
using ClipboardImageEvidenceComponent = Remi.Web.Components.ClipboardImageEvidence;
using ContractRecordView = Remi.Web.Components.ContractRecordView;
using ContractRegistrationPage = Remi.Web.Components.Pages.ContractRegistration;
using Remi.Web.Components.Layout;
using ContractsRegister = Remi.Web.Components.Pages.Contracts;
using DashboardPage = Remi.Web.Components.Pages.Dashboard;
using EvidenceGallery = Remi.Web.Components.EvidenceGallery;
using InvoiceRecordView = Remi.Web.Components.InvoiceRecordView;
using InvoiceRegistrationPage = Remi.Web.Components.Pages.InvoiceRegistration;
using InvoicesRegister = Remi.Web.Components.Pages.Invoices;
using ReportingWorkbookCard = Remi.Web.Components.ReportingWorkbookCard;
using ReportingRegister = Remi.Web.Components.Pages.Reporting;
using SettingsPage = Remi.Web.Components.Pages.Maintenance;
using TemplatesPage = Remi.Web.Components.Pages.Templates;
using Xunit;

namespace Remi.Tests;

public sealed class RegisterComponentTests
{
    private static readonly Guid SampleContractId = Guid.Parse("405b5dd4-0b92-4576-99a9-d2cc7851a2b5");
    private static readonly Guid SampleVasContractId = Guid.Parse("9f2dc10e-9554-47d0-8870-8dbb6bb94e4a");
    private static readonly Guid SampleInvoiceId = Guid.Parse("d461989e-a1e8-4450-a371-31f7f1028df1");
    private static readonly IReadOnlyList<CustomerUrnSuggestion> CustomerDirectoryEntries =
    [
        new("10000001", "Example Borough Council", "Civic Centre, Market Street, Exampleton, EX1 1AA"),
        new("10000002", "Example City Council", "City Hall, High Street, Exampleton, EX2 2BB"),
        new("10000003", "Example County Council", "County Hall, Station Road, Exampleton, EX3 3CC"),
        new("10000004", "North Example Council", "Council House, North Road, Exampleton, EX4 4DD"),
        new("10000005", "South Example Council", "Municipal Offices, South Road, Exampleton, EX5 5EE"),
        new("10000006", "East Example Council", "Town Hall, East Road, Exampleton, EX6 6FF"),
        new("10000007", "West Example Council", "Guildhall, West Road, Exampleton, EX7 7GG"),
    ];

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
    public void Registers_are_direct_navigation_lists_without_selection_controls()
    {
        using var context = CreateContext();

        var contracts = context.Render<ContractsRegister>();
        contracts.WaitForAssertion(() =>
        {
            Assert.Single(contracts.FindAll(".contract-register-table tbody tr"));
            Assert.Empty(contracts.FindAll(".contract-register-table input[type='checkbox']"));
            Assert.Empty(contracts.FindAll(".register-table-toolbar, .register-selection-bar, .register-selection-drawer"));
            Assert.DoesNotContain(contracts.FindAll(".quick-filter"), button => button.TextContent.Trim() == "Selected");
            Assert.Equal(4, contracts.FindAll(".register-filters .floating-field").Count);
            Assert.Empty(contracts.FindAll(".register-filters select"));
            Assert.Equal(3, contracts.FindAll(".register-filters button[role='combobox']").Count);
            Assert.Equal(["All frameworks", "All documents", "Any progress"], contracts.FindAll(".register-filters button[role='combobox']").Select(button => button.TextContent.Trim()).ToList());
        });

        var invoices = context.Render<InvoicesRegister>();
        invoices.WaitForAssertion(() =>
        {
            Assert.Single(invoices.FindAll(".invoice-register-table tbody tr"));
            Assert.Empty(invoices.FindAll(".invoice-register-table input[type='checkbox']"));
            Assert.Empty(invoices.FindAll(".register-table-toolbar, .register-selection-bar, .register-selection-drawer"));
            Assert.DoesNotContain(invoices.FindAll(".quick-filter"), button => button.TextContent.Trim() == "Selected");
            Assert.Equal(4, invoices.FindAll(".register-filters .floating-field").Count);
            Assert.Empty(invoices.FindAll(".register-filters select"));
            Assert.Equal(3, invoices.FindAll(".register-filters button[role='combobox']").Count);
            Assert.Equal(["All frameworks", "All records", "All documents"], invoices.FindAll(".register-filters button[role='combobox']").Select(button => button.TextContent.Trim()).ToList());
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
    public void Settings_opens_frameworks_by_default_and_preserves_explicit_sections()
    {
        using var context = CreateContext();

        var cut = context.Render<SettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Frameworks", cut.Find(".settings-side-nav a.active").TextContent.Trim());
            Assert.Equal("Framework reporting start dates", cut.Find(".settings-content h2").TextContent.Trim());
            Assert.DoesNotContain("Customer URN list", cut.Markup);
        });

        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/settings?section=customer-urns");
        var customerUrns = context.Render<SettingsPage>();
        customerUrns.WaitForAssertion(() =>
        {
            Assert.Equal("Customer URNs", customerUrns.Find(".settings-side-nav a.active").TextContent.Trim());
            Assert.Equal("Customer URN list", customerUrns.Find(".settings-content h2").TextContent.Trim());
        });
    }

    [Fact]
    public async Task Settings_can_define_g_cloud_13_digital_marketplace_mappings_separately()
    {
        using var context = CreateContext();
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/settings?section=digital-marketplace");
        var cut = context.Render<SettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("G-Cloud 14 Digital Marketplace services", cut.Find(".settings-content h2").TextContent.Trim());
            Assert.Equal(2, cut.FindAll("select[aria-label='Digital Marketplace framework'] option").Count);
            Assert.Contains("StatMap Cluster", cut.Markup);
        });

        cut.Find("select[aria-label='Digital Marketplace framework']").Change(FrameworkCode.GCloud13.ToString());
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("G-Cloud 13 Digital Marketplace services", cut.Find(".settings-content h2").TextContent.Trim());
            Assert.Contains("No mappings are configured for G-Cloud 13.", cut.Markup);
            Assert.DoesNotContain("StatMap Cluster", cut.Markup);
        });

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Edit").Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add service").Click();
        cut.Find("input[aria-label='Product name']").Change("Historical StatMap product");
        cut.Find("input[aria-label='Digital Marketplace Service ID']").Change("g13-service-001");
        cut.Find("button.primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("Historical StatMap product", cut.Markup));
        var mappings = await context.Services.GetRequiredService<ReportingWorkspace>()
            .GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud13);
        var mapping = Assert.Single(mappings);
        Assert.Equal("g13-service-001", mapping.ServiceId);
        Assert.Equal(FrameworkCode.GCloud13, mapping.Framework);
    }

    [Fact]
    public void Invoice_contract_picklist_opens_with_every_item_and_filters_as_the_user_types()
    {
        using var context = CreateContext(additionalContracts: 6);
        var registration = context.Render<InvoiceRegistrationPage>();
        var picker = registration.Find("input[role='combobox'][aria-label='Contract']");

        picker.Focus();

        registration.WaitForAssertion(() =>
        {
            Assert.Equal("true", picker.GetAttribute("aria-expanded"));
            Assert.Equal(7, registration.FindAll("#contract-suggestions [role='option']").Count);
        });

        picker.Input("EXTRA-004");

        registration.WaitForAssertion(() =>
        {
            var matches = registration.FindAll("#contract-suggestions [role='option']");
            Assert.Single(matches);
            Assert.Contains("EXTRA-004", matches[0].TextContent);
        });

        picker.Input(string.Empty);
        registration.WaitForAssertion(() => Assert.Equal(7, registration.FindAll("#contract-suggestions [role='option']").Count));

        picker.Blur();
        registration.WaitForAssertion(() =>
        {
            Assert.Equal("false", registration.Find("input[role='combobox'][aria-label='Contract']").GetAttribute("aria-expanded"));
            Assert.Empty(registration.FindAll("#contract-suggestions"));
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

        var framework = registration.Find("button[role='combobox'][aria-label='Framework']");
        framework.Click();
        Assert.Equal(
            ["G-Cloud 14 (RM1557.14)", "Vertical Application Solutions (RM6259)"],
            registration.FindAll("#framework-picklist-options [role='option']").Select(option => option.TextContent.Trim()).ToList());
        registration.FindAll("#framework-picklist-options [role='option']").Single(option => option.TextContent.Contains("G-Cloud 14")).Click();

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
            Assert.True(serviceSection.QuerySelector("input[aria-label='Service Group']")!.HasAttribute("disabled"));
            var servicePicker = serviceSection.QuerySelector("input[role='combobox'][aria-label='Digital Marketplace Service ID']")!;
            Assert.Equal("marketplace-service-suggestions", servicePicker.GetAttribute("aria-controls"));
            Assert.Null(servicePicker.GetAttribute("list"));
            Assert.Empty(registration.FindAll("select"));
        });

        registration.Find("input[aria-label='Digital Marketplace Service ID']").Focus();
        registration.WaitForAssertion(() =>
        {
            var option = registration.Find("#marketplace-service-suggestions [role='option']");
            Assert.Contains("115981361947474", option.TextContent);
            Assert.Contains("StatMap Cluster", option.TextContent);
        });

        registration.Find("button[role='combobox'][aria-label='Lot number']").Click();
        registration.FindAll("#lot-number-picklist-options [role='option']").Single(option => option.TextContent.Trim() == "2").Click();

        var serviceGroup = registration.Find("input[role='combobox'][aria-label='Service Group']");
        Assert.False(serviceGroup.HasAttribute("disabled"));
        serviceGroup.Focus();
        registration.WaitForAssertion(() => Assert.Contains("Information and Communication Technology (ICT)", registration.Find("#service-group-picklist-options").TextContent));
    }

    [Fact]
    public void Contract_registration_uses_one_compact_context_and_validates_when_save_is_requested()
    {
        using var context = CreateContext();
        var registration = context.Render<ContractRegistrationPage>();

        Assert.Empty(registration.FindAll(".invoice-intake-header .invoice-command"));
        Assert.Empty(registration.FindAll(".invoice-contract-section h2"));
        Assert.Empty(registration.FindAll(".clipboard-image-panel > header"));
        Assert.Equal("Supporting documents", registration.Find(".clipboard-document-dropzone strong").TextContent.Trim());
        Assert.Contains("choose a file or photo", registration.Find(".clipboard-document-dropzone").TextContent);

        registration.Find("button[role='combobox'][aria-label='Framework']").Click();
        registration.FindAll("#framework-picklist-options [role='option']").Single(option => option.TextContent.Contains("G-Cloud 14")).Click();

        registration.WaitForAssertion(() =>
        {
            Assert.Empty(registration.FindAll(".contract-framework-summary"));
            Assert.False(registration.Find("button.invoice-command-save").HasAttribute("disabled"));
        });

        registration.Find("button.invoice-command-save").Click();

        registration.WaitForAssertion(() =>
        {
            Assert.Contains("Complete the highlighted fields", registration.Find(".contract-registration-validation").TextContent);
            Assert.Equal(8, registration.FindAll("[aria-invalid='true']").Count);
            Assert.Equal(8, registration.FindAll(".field-validation-message").Count);
            Assert.Equal("Enter the supplier reference number.", registration.Find("#supplier-reference-error").TextContent.Trim());
        });
    }

    [Fact]
    public void Contract_registration_replaces_long_native_lists_with_searchable_Remi_picklists()
    {
        using var context = CreateContext();
        var registration = context.Render<ContractRegistrationPage>();

        registration.Find("button[role='combobox'][aria-label='Framework']").Click();
        registration.FindAll("#framework-picklist-options [role='option']").Single(option => option.TextContent.Contains("G-Cloud 14")).Click();

        var reportingMonth = registration.Find("input[role='combobox'][aria-label='Reporting month']");
        reportingMonth.Focus();
        registration.WaitForAssertion(() =>
        {
            Assert.Equal(61, registration.FindAll("#reporting-month-picklist-options [role='option']").Count);
            Assert.Equal("true", registration.Find("#reporting-month-picklist-options").GetAttribute("data-scroll-selected"));
            Assert.Single(registration.FindAll("#reporting-month-picklist-options [aria-selected='true']"));
            Assert.Empty(registration.FindAll("#lot-number-picklist-options"));
        });

        reportingMonth.Input("February 2025");
        registration.WaitForAssertion(() =>
        {
            var option = registration.Find("#reporting-month-picklist-options [role='option']");
            Assert.Equal("February 2025", option.TextContent.Trim());
        });
        registration.Find("#reporting-month-picklist-options [role='option']").Click();
        registration.WaitForAssertion(() => Assert.Equal("February 2025", registration.Find("input[aria-label='Reporting month']").GetAttribute("value")));

        registration.Find("button[role='combobox'][aria-label='Lot number']").Click();
        registration.FindAll("#lot-number-picklist-options [role='option']").Single(option => option.TextContent.Trim() == "2").Click();

        var serviceGroup = registration.Find("input[role='combobox'][aria-label='Service Group']");
        serviceGroup.Input("Information and Communication Technology");
        registration.WaitForAssertion(() => Assert.Single(registration.FindAll("#service-group-picklist-options [role='option']")));
        registration.Find("#service-group-picklist-options [role='option']").Click();

        registration.WaitForAssertion(() =>
        {
            Assert.Equal("Information and Communication Technology (ICT)", registration.Find("input[aria-label='Service Group']").GetAttribute("value"));
            Assert.Empty(registration.FindAll("select"));
        });
    }

    [Fact]
    public void Contract_registration_links_both_customer_fields_to_the_full_Gca_directory_and_shows_the_selected_address()
    {
        using var context = CreateContext();
        var registration = context.Render<ContractRegistrationPage>();
        registration.Find("button[role='combobox'][aria-label='Framework']").Click();
        registration.FindAll("#framework-picklist-options [role='option']").Single(option => option.TextContent.Contains("G-Cloud 14")).Click();

        var customerName = registration.Find("input[role='combobox'][aria-label='Customer organisation name']");
        customerName.Focus();

        registration.WaitForAssertion(() =>
        {
            Assert.Equal("true", customerName.GetAttribute("aria-expanded"));
            Assert.Equal(7, registration.FindAll("#customer-name-suggestions [role='option']").Count);
            Assert.Empty(registration.FindAll("#lot-number-picklist-options"));
        });

        customerName.Blur();
        registration.WaitForAssertion(() => Assert.Empty(registration.FindAll("#customer-name-suggestions")));
        customerName.Focus();

        customerName.Input("Example City");
        registration.WaitForAssertion(() => Assert.Single(registration.FindAll("#customer-name-suggestions [role='option']")));
        registration.Find("#customer-name-suggestions [role='option']").Click();

        registration.WaitForAssertion(() =>
        {
            Assert.Equal("Example City Council", registration.Find("input[aria-label='Customer organisation name']").GetAttribute("value"));
            Assert.Equal("10000002", registration.Find("input[aria-label='Customer Unique Reference Number (URN)']").GetAttribute("value"));
            Assert.Contains("City Hall, High Street, Exampleton, EX2 2BB", registration.Find(".customer-address-verification").TextContent);
        });

        var customerUrn = registration.Find("input[role='combobox'][aria-label='Customer Unique Reference Number (URN)']");
        customerUrn.Input("10000001");
        registration.WaitForAssertion(() => Assert.Single(registration.FindAll("#customer-urn-suggestions [role='option']")));
        registration.Find("#customer-urn-suggestions [role='option']").Click();

        registration.WaitForAssertion(() =>
        {
            Assert.Equal("Example Borough Council", registration.Find("input[aria-label='Customer organisation name']").GetAttribute("value"));
            Assert.Contains("Civic Centre, Market Street, Exampleton, EX1 1AA", registration.Find(".customer-address-verification").TextContent);
        });
    }

    [Fact]
    public void Marketplace_service_picklist_opens_with_every_service_and_filters_by_id_or_name()
    {
        using var context = CreateContext(additionalMarketplaceServices: 6);
        var registration = context.Render<ContractRegistrationPage>();
        registration.Find("button[role='combobox'][aria-label='Framework']").Click();
        registration.FindAll("#framework-picklist-options [role='option']").Single(option => option.TextContent.Contains("G-Cloud 14")).Click();
        var picker = registration.Find("input[role='combobox'][aria-label='Digital Marketplace Service ID']");

        picker.Focus();
        registration.WaitForAssertion(() => Assert.Equal(7, registration.FindAll("#marketplace-service-suggestions [role='option']").Count));

        picker.Input("Picklist service 004");
        registration.WaitForAssertion(() =>
        {
            var option = registration.Find("#marketplace-service-suggestions [role='option']");
            Assert.Contains("SERVICE-004", option.TextContent);
        });

        picker.Blur();
        registration.WaitForAssertion(() => Assert.Empty(registration.FindAll("#marketplace-service-suggestions")));
    }

    [Fact]
    public async Task Contract_registration_document_cards_use_an_accessible_icon_remove_action()
    {
        using var context = CreateContext();
        var registration = context.Render<ContractRegistrationPage>();
        var evidence = registration.FindComponent<ClipboardImageEvidenceComponent>();

        await evidence.InvokeAsync(() => evidence.Instance.DocumentAdded("contract-file", "signed-contract.pdf", "application/pdf", 128, null));

        registration.WaitForAssertion(() =>
        {
            var remove = registration.Find(".clipboard-document-item .clipboard-document-remove");
            Assert.Equal("×", remove.TextContent.Trim());
            Assert.Equal("Remove signed-contract.pdf", remove.GetAttribute("aria-label"));
            Assert.Equal("Remove document", remove.GetAttribute("title"));
        });
    }

    [Fact]
    public void Vas_contract_registration_uses_only_the_vas_template_fields_and_order_channel_lookup()
    {
        using var context = CreateContext();
        var registration = context.Render<ContractRegistrationPage>();

        registration.Find("button[role='combobox'][aria-label='Framework']").Click();
        registration.FindAll("#framework-picklist-options [role='option']").Single(option => option.TextContent.Contains("Vertical Application Solutions")).Click();

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
            Assert.Empty(registration.FindAll("select"));
        });

        registration.Find("button[role='combobox'][aria-label='Order Channel']").Click();
        Assert.Equal(
            ["Direct Award", "Further Competition"],
            registration.FindAll("#order-channel-picklist-options [role='option']").Select(option => option.TextContent.Trim()).ToList());
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
            Assert.Equal("Prepare", dashboardHeader.QuerySelector("a.remi-action--primary")?.TextContent.Trim());

            var readinessHeader = cut.Find(".dashboard-readiness .dashboard-section-heading");
            Assert.Equal("Return readiness", readinessHeader.QuerySelector("h2")?.TextContent.Trim());
            Assert.Contains("Frameworks included in the July 2026 reporting period.", readinessHeader.TextContent);

            var attentionHeader = cut.Find(".dashboard-attention .dashboard-section-heading");
            Assert.Equal("Needs attention", attentionHeader.QuerySelector("h2")?.TextContent.Trim());

            var activityHeader = cut.Find(".dashboard-activity .dashboard-section-heading");
            Assert.Equal("Recent activity", activityHeader.QuerySelector("h2")?.TextContent.Trim());
            Assert.Equal("View", activityHeader.QuerySelector("a.remi-action--section")?.TextContent.Trim());

            var tableHeaders = cut.FindAll(".dashboard-table th").Select(header => header.TextContent.Trim()).ToList();
            Assert.Equal(["Framework", "Contracts", "Invoices", "Readiness", "Action"], tableHeaders);
            Assert.All(cut.FindAll(".dashboard-row-action"), action =>
                Assert.Matches("^/reports/\\d+/2026-07\\?period=2026-07$", action.GetAttribute("href")));
            Assert.All(cut.FindAll(".dashboard-table td.table-action-cell"), cell =>
                Assert.DoesNotContain("→", cell.TextContent));
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
            Assert.Empty(cut.FindAll(".contract-hero-actions a"));
            Assert.Equal(4, cut.FindAll(".record-display-grid").Count);
            Assert.Contains("Operational delivery", cut.Markup);
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
            var servicePicker = cut.Find(".contract-edit-panel input[role='combobox'][aria-label='Digital Marketplace Service ID']");
            Assert.Equal("edit-marketplace-service-suggestions", servicePicker.GetAttribute("aria-controls"));
            Assert.Null(servicePicker.GetAttribute("list"));
        });

        cut.Find(".contract-edit-panel input[aria-label='Digital Marketplace Service ID']").Focus();
        cut.WaitForAssertion(() => Assert.Contains("115981361947474", cut.Find("#edit-marketplace-service-suggestions [role='option']").TextContent));
    }

    [Fact]
    public void Contract_editing_uses_the_same_linked_Gca_customer_picker_as_registration()
    {
        using var context = CreateContext();
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(component => component.ContractId, SampleContractId));
        cut.WaitForAssertion(() => Assert.Equal("Edit", cut.Find(".contract-hero-actions button.secondary").TextContent.Trim()));
        cut.Find(".contract-hero-actions button.secondary").Click();

        var customerName = cut.Find(".contract-edit-panel input[role='combobox'][aria-label='Customer organisation name']");
        customerName.Focus();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll("#edit-customer-name-suggestions [role='option']").Count));

        customerName.Blur();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#edit-customer-name-suggestions")));
        customerName.Focus();

        customerName.Input("Example County");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#edit-customer-name-suggestions [role='option']")));
        cut.Find("#edit-customer-name-suggestions [role='option']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Example County Council", cut.Find(".contract-edit-panel input[aria-label='Customer organisation name']").GetAttribute("value"));
            Assert.Equal("10000003", cut.Find(".contract-edit-panel input[aria-label='Customer Unique Reference Number (URN)']").GetAttribute("value"));
            Assert.Contains("County Hall, Station Road, Exampleton, EX3 3CC", cut.Find(".contract-edit-panel .customer-address-verification").TextContent);
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
            Assert.Empty(cut.FindAll(".invoice-record .contract-breadcrumbs"));
            Assert.Equal("Edit", cut.Find(".invoice-record .contract-hero-actions button.secondary").TextContent.Trim());
            Assert.Empty(cut.FindAll(".invoice-record .contract-hero-actions a"));
            Assert.Empty(cut.FindAll(".invoice-overview-actions button.secondary"));
            Assert.DoesNotContain("View →", cut.Markup);
            Assert.DoesNotContain("Back ←", cut.Markup);
        });

        cut.Find(".invoice-record .contract-hero-actions button.secondary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".record-display-grid"));
            Assert.Equal(19, cut.FindAll(".contract-edit-panel .floating-field").Count);
            Assert.Equal(["Cancel", "Save"], cut.Find(".contract-edit-panel .invoice-overview-actions").QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
            Assert.Single(cut.FindAll(".invoice-edit-evidence-layout .clipboard-document-dropzone input[accept*='image']"));
            Assert.Equal("LABEL", cut.Find(".invoice-edit-evidence-layout .clipboard-document-dropzone").TagName);
            Assert.Contains("press Ctrl+V to paste an image", cut.Find(".invoice-edit-evidence-layout .clipboard-document-dropzone").TextContent);
        });
    }

    [Fact]
    public void Document_cards_open_a_Casey_style_preview_with_download_inside_the_dialog()
    {
        using var context = CreateContext();
        var evidenceId = Guid.Parse("f2ac7a1a-59e8-4894-9f95-40c7019a364a");
        IReadOnlyList<EvidenceLink> documents =
        [
            new(evidenceId, EvidenceKind.ContractDocument, "signed-contract.pdf", "contracts/signed-contract.pdf", "application/pdf", 1536, "2026-07", DateTimeOffset.UtcNow),
        ];
        var cut = context.Render<EvidenceGallery>(parameters => parameters.Add(component => component.Documents, documents));

        var card = cut.Find(".remi-document-card");
        Assert.Equal("BUTTON", card.QuerySelector(".remi-document-open")!.TagName);
        Assert.Equal("Open signed-contract.pdf", card.QuerySelector(".remi-document-open")!.GetAttribute("aria-label"));
        Assert.Equal("PDF", card.QuerySelector(".remi-document-thumbnail")!.TextContent.Trim());
        Assert.Equal("signed-contract", card.QuerySelector(".remi-document-name")!.TextContent.Trim());
        Assert.Equal("1.5 KB", card.QuerySelector(".remi-document-meta")!.TextContent.Trim());
        Assert.DoesNotContain("Download", card.TextContent);

        card.QuerySelector(".remi-document-open")!.Click();

        var dialog = cut.Find("[role='dialog'][aria-modal='true']");
        Assert.Equal($"/evidence/{evidenceId}/preview", dialog.QuerySelector("iframe")!.GetAttribute("src"));
        Assert.Equal($"/evidence/{evidenceId}/download", dialog.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("Download", dialog.QuerySelector("a")!.TextContent.Trim());
        dialog.QuerySelector("button[aria-label='Close preview']")!.Click();
        Assert.Empty(cut.FindAll("[role='dialog']"));
    }

    [Fact]
    public void Document_preview_policy_matches_Caseys_supported_formats()
    {
        Assert.Equal(EvidencePreviewKind.Image, EvidencePreviewPolicy.GetKind("image.png"));
        Assert.Equal(EvidencePreviewKind.Image, EvidencePreviewPolicy.GetKind("image.jpeg"));
        Assert.Equal(EvidencePreviewKind.Image, EvidencePreviewPolicy.GetKind("image.gif"));
        Assert.Equal(EvidencePreviewKind.Image, EvidencePreviewPolicy.GetKind("image.webp"));
        Assert.Equal(EvidencePreviewKind.Pdf, EvidencePreviewPolicy.GetKind("contract.pdf"));
        Assert.Equal(EvidencePreviewKind.Text, EvidencePreviewPolicy.GetKind("notes.txt"));
        Assert.Equal(EvidencePreviewKind.Text, EvidencePreviewPolicy.GetKind("data.csv"));
        Assert.Equal(EvidencePreviewKind.None, EvidencePreviewPolicy.GetKind("contract.docx"));
    }

    [Fact]
    public void Invoice_register_uses_the_designation_as_its_only_record_opening_control()
    {
        using var context = CreateContext();
        var cut = context.Render<InvoicesRegister>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".invoice-register-table tbody tr")));
        var row = cut.Find(".invoice-register-table tbody tr");

        Assert.Equal("Invoice designation ↓", cut.FindAll(".invoice-register-table th")[0].TextContent.Trim());
        Assert.Null(row.GetAttribute("tabindex"));
        Assert.Single(row.QuerySelectorAll("a.register-reference"));
        Assert.Empty(row.QuerySelectorAll(".table-action-cell"));
        Assert.Empty(row.QuerySelectorAll(".remi-action"));
        Assert.Equal("Check ↕", cut.FindAll(".invoice-register-table th")[5].TextContent.Trim());
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
            Assert.Equal(4, workspace.FindAll("[role='tablist'] .return-workflow-tab").Count);
            Assert.DoesNotContain("Monthly MI workbook", workspace.Markup);
            Assert.Empty(workspace.FindAll("input[type='file']"));
        });

        workspace.Find("button[data-stage='2']").Click();

        workspace.WaitForAssertion(() =>
        {
            Assert.Equal("true", workspace.Find("button[data-stage='2']").GetAttribute("aria-selected"));
            Assert.Contains("Create the workbook that will be uploaded", workspace.Find(".return-stage-panel").TextContent);
            Assert.DoesNotContain("Generated files", workspace.Markup);
        });
    }

    [Fact]
    public void Report_workflow_tabs_keep_each_stage_available_without_bypassing_its_prerequisites()
    {
        using var context = CreateContext();
        var workspace = context.Render<ReportingRegister>(parameters => parameters
            .Add(component => component.FrameworkValue, (int)FrameworkCode.GCloud14)
            .Add(component => component.WorkspaceMonth, "2026-07"));

        workspace.WaitForAssertion(() =>
        {
            Assert.Equal("true", workspace.Find("button[data-stage='1']").GetAttribute("aria-selected"));
            Assert.Contains("Checks", workspace.Find(".return-stage-panel").TextContent);
        });

        workspace.Find("button[data-stage='3']").Click();
        workspace.WaitForAssertion(() => Assert.Contains("Generate the reporting workbook before uploading", workspace.Find(".return-stage-panel").TextContent));

        workspace.Find("button[data-stage='4']").Click();
        workspace.WaitForAssertion(() => Assert.Contains("Complete the GCA upload stage", workspace.Find(".return-stage-panel").TextContent));

        workspace.Find("button[data-stage='1']").Click();
        workspace.WaitForAssertion(() => Assert.Contains("Report contents", workspace.Find(".return-stage-panel").TextContent));
    }

    [Fact]
    public async Task Invoice_deletion_requires_confirmation_and_returns_to_the_period_register()
    {
        using var context = CreateContext();
        var cut = context.Render<InvoiceRecordView>(parameters => parameters.Add(component => component.InvoiceId, SampleInvoiceId));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Delete", cut.Find("button.invoice-delete-trigger").TextContent.Trim());
            Assert.Empty(cut.FindAll("button.invoice-delete-confirm"));
        });

        cut.Find("button.invoice-delete-trigger").Click();
        Assert.Contains("permanently removes the invoice", cut.Find(".invoice-delete-confirmation").TextContent);
        cut.Find("button.invoice-delete-confirm").Click();

        cut.WaitForAssertion(() => Assert.EndsWith(
            "/invoices?period=2026-07",
            context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri,
            StringComparison.Ordinal));
        Assert.Null(await context.Services.GetRequiredService<ReportingWorkspace>().GetInvoiceDetailsAsync(SampleInvoiceId));
    }

    [Fact]
    public void Payment_schedule_editing_replaces_the_read_only_table_with_a_compact_grid()
    {
        using var context = CreateContext(includePaymentSchedule: true);
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(component => component.ContractId, SampleContractId));

        cut.WaitForAssertion(() =>
        {
            var scheduleSection = cut.FindAll(".contract-detail-section").Single(section => section.QuerySelector("h3")?.TextContent.Trim() == "Payment schedule");
            scheduleSection.QuerySelector(".contract-section-actions button")!.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var scheduleSection = cut.FindAll(".contract-detail-section").Single(section => section.QuerySelector("h3")?.TextContent.Trim() == "Payment schedule");
            Assert.Empty(scheduleSection.QuerySelectorAll(".table-wrap"));
            Assert.Equal(["Year", "Description", "Expected date", "Value, ex VAT", "Optional"], scheduleSection.QuerySelectorAll(".payment-schedule-heading span").Take(5).Select(item => item.TextContent.Trim()));
            Assert.Equal(2, scheduleSection.QuerySelectorAll(".payment-position-row").Length);
            Assert.Equal(2, scheduleSection.QuerySelectorAll(".payment-position-remove").Length);
            Assert.Equal(["Add", "Save", "Cancel"], scheduleSection.QuerySelectorAll(".contract-section-actions button").Select(button => button.TextContent.Trim()));
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
            Assert.Equal("Return totals", summary.GetAttribute("aria-label"));
            Assert.Contains("RM6259", workspace.Find(".return-workspace-header .lede").TextContent);
            Assert.DoesNotContain("reporting summary", workspace.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Invoices", summary.TextContent);
            Assert.Contains("Purchase order", summary.TextContent);
            Assert.Contains("GCA_VAS_202607", summary.TextContent);
            Assert.Equal("Submission workflow", workspace.Find(".return-workflow-heading h2").TextContent.Trim());
        });
    }

    [Fact]
    public void Reporting_workbook_card_uses_one_compact_download_presentation()
    {
        using var context = CreateContext();
        var workbook = new ReportingEvidence(
            Guid.Parse("5d948fa9-07a4-4b2c-b189-604e81d90fd7"),
            EvidenceKind.GeneratedMiWorkbook,
            "RM1557.14-MI-2026-07.xlsx",
            "generated/RM1557.14-MI-2026-07.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            28656,
            null,
            new DateTimeOffset(2026, 8, 6, 15, 44, 0, TimeSpan.Zero));

        var card = context.Render<ReportingWorkbookCard>(parameters => parameters.Add(component => component.Workbook, workbook));

        Assert.Equal("RM1557.14-MI-2026-07.xlsx", card.Find(".return-workbook-card-copy strong").TextContent.Trim());
        Assert.Contains("Generated", card.Find(".return-workbook-card-copy span").TextContent);
        Assert.Contains("28.0 KB", card.Find(".return-workbook-card-copy span").TextContent);
        Assert.Equal(workbook.ArchivedAtUtc.ToString("O"), card.Find("time").GetAttribute("datetime"));
        var download = card.Find("a");
        Assert.Equal("Download workbook", download.TextContent.Trim());
        Assert.Equal($"/evidence/{workbook.Id}", download.GetAttribute("href"));
        Assert.Single(card.FindAll("a"));
    }

    [Fact]
    public void Submitted_return_separates_its_evidence_view_from_editing_submission_details()
    {
        using var context = CreateContext(includeSubmittedReturn: true);
        var workspace = context.Render<ReportingRegister>(parameters => parameters
            .Add(component => component.FrameworkValue, (int)FrameworkCode.GCloud13)
            .Add(component => component.WorkspaceMonth, "2026-07"));

        workspace.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("? Help", workspace.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Back to reports", workspace.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GCA_G13_202607", workspace.Find(".gca-return-summary").TextContent);
            Assert.NotNull(workspace.Find(".return-submission-evidence-view").QuerySelector("img[src^='/evidence/']"));
            Assert.Empty(workspace.FindAll(".return-workflow-tab-mark"));
            Assert.Equal(3, workspace.FindAll(".return-workflow-tab-status").Count);
            Assert.Equal("true", workspace.Find("button[data-stage='4']").GetAttribute("aria-selected"));
            var metadata = workspace.Find(".return-submission-metadata");
            Assert.Contains("e0303f95-3441-4d48-bd32-e028a66f87db", metadata.TextContent);
            Assert.Contains("6 August 2026 13:45 UTC", metadata.TextContent);
            Assert.Contains("Open GCA submission", metadata.TextContent);
            Assert.Empty(workspace.FindAll("input[type='file']"));
        });

        workspace.Find(".return-submission-record-actions button").Click();

        workspace.WaitForAssertion(() =>
        {
            Assert.Empty(workspace.FindAll(".return-submission-record-grid"));
            Assert.Equal("6 August 2026 13:45 UTC", workspace.Find("input[aria-label='Submission date and time']").GetAttribute("value"));
            Assert.Single(workspace.FindAll("input[type='file']"));
            var evidenceIntake = workspace.Find(".return-submission-evidence-layout");
            Assert.Empty(evidenceIntake.QuerySelectorAll(".clipboard-image-panel > header"));
            Assert.Contains("clipboard-image-panel--plain", evidenceIntake.QuerySelector(".clipboard-image-panel")!.ClassList);
            Assert.Equal("Submission evidence", evidenceIntake.QuerySelector(".clipboard-document-dropzone strong")!.TextContent.Trim());
            Assert.Contains("Add the GCA submission confirmation screenshot.", evidenceIntake.QuerySelector(".clipboard-documents-empty")!.TextContent);
            Assert.Contains("Retained documents", workspace.Find(".submission-document-editor").TextContent);
            Assert.Equal("gca-confirmation", workspace.Find("input[aria-label='Document title for gca-confirmation.png']").GetAttribute("value"));
            Assert.Equal("Remove", workspace.Find("button[aria-label='Remove gca-confirmation.png']").TextContent.Trim());
        });

        workspace.Find("button[aria-label='Remove gca-confirmation.png']").Click();

        workspace.WaitForAssertion(() =>
        {
            Assert.True(workspace.Find("input[aria-label='Document title for gca-confirmation.png']").HasAttribute("disabled"));
            Assert.Contains("permanently deleted when you save", workspace.Find(".submission-document-removal-note").TextContent);
            Assert.False(workspace.Find(".submission-evidence-save .remi-action--primary").HasAttribute("disabled"));
        });

        workspace.Find("button[aria-label='Undo removal of gca-confirmation.png']").Click();

        workspace.WaitForAssertion(() => Assert.True(workspace.Find(".submission-evidence-save .remi-action--primary").HasAttribute("disabled")));
        workspace.Find("input[aria-label='Document title for gca-confirmation.png']").Input("GCA nil-return confirmation");

        workspace.WaitForAssertion(() => Assert.False(workspace.Find(".submission-evidence-save .remi-action--primary").HasAttribute("disabled")));
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

    private static BunitContext CreateContext(
        FrameworkCode? additionalFramework = null,
        bool includePaymentSchedule = false,
        int additionalContracts = 0,
        int additionalMarketplaceServices = 0,
        bool includeSubmittedReturn = false)
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
        if (includeSubmittedReturn)
        {
            var returnId = Guid.NewGuid();
            var submittedAt = new DateTimeOffset(2026, 8, 6, 13, 45, 0, TimeSpan.Zero);
            database.MonthlyReturns.Add(new MonthlyReturn(
                returnId,
                FrameworkCode.GCloud13,
                "2026-07",
                ReturnStatus.NilReturn,
                submittedAt,
                "e0303f95-3441-4d48-bd32-e028a66f87db",
                null,
                submittedAt));
            database.Evidence.Add(new EvidenceRecord(
                Guid.NewGuid(),
                EvidenceKind.SubmissionEvidence,
                FrameworkCode.GCloud13,
                "2026-07",
                "gca-confirmation.png",
                $"clipboard/monthly-return/{returnId:D}/gca-confirmation.png",
                "evidence/gca-confirmation.png",
                "image/png",
                2048,
                new string('a', 64),
                null,
                submittedAt.AddMinutes(1)));
        }
        for (var index = 1; index <= additionalContracts; index++)
        {
            database.Contracts.Add(new ContractRecord(
                Guid.NewGuid(),
                FrameworkCode.GCloud14,
                $"EXTRA-{index:000}",
                $"Picklist customer {index:000}",
                $"URN-EXTRA-{index:000}",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 12, 31),
                "2",
                "Information and Communication Technology (ICT)",
                null,
                null,
                null,
                $"SERVICE-{index:000}",
                1000 + index,
                "2026-07",
                $"extra-{index:000}.xlsx",
                DateTimeOffset.UtcNow));
        }
        for (var index = 1; index <= additionalMarketplaceServices; index++)
        {
            database.DigitalMarketplaceServices.Add(new DigitalMarketplaceService(
                $"SERVICE-{index:000}",
                $"Picklist service {index:000}"));
        }
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
        if (includePaymentSchedule)
        {
            database.ChargeScheduleItems.AddRange(
            [
                new ChargeScheduleItem(Guid.NewGuid(), SampleContractId, null, 1, "Annual licence and maintenance", new DateOnly(2026, 1, 1), 32910, false, DateTimeOffset.UtcNow),
                new ChargeScheduleItem(Guid.NewGuid(), SampleContractId, null, 2, "Annual licence and maintenance", new DateOnly(2027, 1, 1), 30683.40m, true, DateTimeOffset.UtcNow),
            ]);
        }
        var reportingPeriod = new ReportingPeriodContext(TimeProvider.System);
        reportingPeriod.Synchronise(["2026-07"], "2026-07");
        var context = new BunitContext();
        context.Services.AddSingleton(reportingPeriod);
        context.Services.AddSingleton<IRemiDataTransfer>(new StubDataTransfer());
        context.Services.AddSingleton(new ReportingWorkspace(
            new InMemoryStore(database),
            null!,
            null!,
            null!,
            new InMemoryCustomerUrnDirectory(CustomerDirectoryEntries),
            TimeProvider.System));
        return context;
    }

    private sealed class InMemoryCustomerUrnDirectory(IReadOnlyList<CustomerUrnSuggestion> entries) : ICustomerUrnDirectory
    {
        public Task<CustomerUrnDirectoryStatus?> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CustomerUrnDirectoryStatus?>(null);

        public Task<IReadOnlyList<CustomerUrnSuggestion>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(entries);

        public Task<IReadOnlyList<CustomerUrnSuggestion>> SearchAsync(
            string query,
            int maximumResults = 8,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CustomerUrnSuggestion>>(entries
                .Where(item => item.OrganisationName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Urn.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (item.Address?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(maximumResults)
                .ToList());

        public Task<CustomerUrnDirectoryRefresh> RefreshAsync(Guid evidenceId, CancellationToken cancellationToken = default) =>
            Task.FromException<CustomerUrnDirectoryRefresh>(new NotSupportedException());
    }

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) =>
            Task.FromResult(update(database));
    }

    private sealed class StubDataTransfer : IRemiDataTransfer
    {
        public Task<PreparedDataTransfer> PrepareExportAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedDataTransfer? GetPreparedExport(Guid id) => null;

        public Task<Stream?> OpenPreparedExportAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task DiscardPreparedExportAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExportAsync(Stream destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ImportAsync(Stream source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
