using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web.Components;
using DashboardPage = Remi.Web.Components.Pages.Dashboard;
using InvoiceRegistrationPage = Remi.Web.Components.Pages.InvoiceRegistration;
using Xunit;

namespace Remi.Tests;

public sealed partial class RegisterComponentTests
{
    [Fact]
    public async Task Non_framework_registration_saves_a_verbal_rolling_agreement_without_framework_fields()
    {
        using var context = CreateContext();
        var today = ContractPortfolioRules.Today(context.Services.GetRequiredService<TimeProvider>());
        var form = context.Render<NonFrameworkContractForm>();
        form.Find("input[aria-label='Contract reference']").Change("NF-UI-001");
        form.Find("input[aria-label='Customer name']").Change("Private Customer");
        form.Find("input[aria-label='Contract start date']").Change(today.AddYears(-1).ToString("yyyy-MM-dd"));
        form.Find("input[aria-label='Initial annual charge ex VAT']").Change("10000");
        form.Find("textarea[aria-label='Agreement notes']").Change("Verbal licence and maintenance agreement with Private Customer.");
        form.Find("input[aria-label='Additional percentage']").Change("4");
        form.Find("input[aria-label='CPI reference']").Change("Annual CPI for preceding December");
        Assert.Empty(form.FindAll("input[aria-label='Contract end date']"));
        Assert.DoesNotContain("Reporting month", form.Markup);
        form.Find(".non-framework-form button.remi-action--primary").Click();
        form.WaitForAssertion(() => Assert.Contains("Non-framework agreement saved", form.Find("[role='status']").TextContent));
        var store = context.Services.GetRequiredService<IRemiStore>();
        var contract = await store.ReadAsync(database => database.Contracts.Single(item => item.SupplierReference == "NF-UI-001"));
        Assert.Equal(AgreementBasis.Verbal, contract.NonFrameworkTerms!.AgreementBasis);
        Assert.Equal(ContractTermKind.AnnualRolling, contract.NonFrameworkTerms.TermKind);
        Assert.Null(contract.EndDate);
        Assert.DoesNotContain(await store.ReadAsync(database => database.MonthlyReturns.ToList()), item => item.Framework == FrameworkCode.NonFramework);
    }

    [Fact]
    public async Task Non_framework_detail_reviews_Cpi_and_shows_renewal_deadlines_instead_of_MI_fields()
    {
        using var context = CreateContext();
        var workspace = context.Services.GetRequiredService<ReportingWorkspace>();
        var today = ContractPortfolioRules.Today(context.Services.GetRequiredService<TimeProvider>());
        var entry = NonFrameworkUiEntry(today);
        var saved = await workspace.SaveNonFrameworkContractAsync(entry);
        Assert.True(saved.Succeeded, saved.Message);
        var id = saved.EntityId!.Value;
        var record = context.Render<ContractRecordView>(parameters => parameters.Add(component => component.ContractId, id));
        record.WaitForAssertion(() => Assert.Contains("Notice deadline (90 days)", record.Markup));
        Assert.DoesNotContain("Digital Marketplace", record.Markup);
        Assert.Contains("No set end date", record.Markup);
        record.Find("input[aria-label='CPI rate for annual review']").Change("3");
        record.Find("input[aria-label='CPI source and period']").Change("Actual CPI annual rate for December");
        record.WaitForAssertion(() => Assert.Contains("10,700.00", record.Markup));
        record.Find(".non-framework-record button.remi-action--primary").Click();
        record.WaitForAssertion(() => Assert.Contains("Annual charge reviewed", record.Find("[role='status']").TextContent));
        Assert.Single((await workspace.GetContractDetailsAsync(id))!.AnnualReviews);
        var home = context.Render<DashboardPage>();
        home.WaitForAssertion(() => Assert.Contains("Rolling renewals and notice deadlines", home.Markup));
        Assert.Contains("NF-UI", home.Markup);
    }

    [Fact]
    public async Task Non_framework_invoice_registration_and_editing_require_no_MI_fields_and_keep_payment_tracking()
    {
        using var context = CreateContext();
        var workspace = context.Services.GetRequiredService<ReportingWorkspace>();
        var today = ContractPortfolioRules.Today(context.Services.GetRequiredService<TimeProvider>());
        var saved = await workspace.SaveNonFrameworkContractAsync(NonFrameworkUiEntry(today));
        Assert.True(saved.Succeeded, saved.Message);
        var registration = context.Render<InvoiceRegistrationPage>();
        registration.Find("input[aria-label='Contract']").Input("NF-UI");
        registration.WaitForAssertion(() => Assert.Single(registration.FindAll("button[role='option']")));
        registration.Find("button[role='option']").Click();
        registration.WaitForAssertion(() => Assert.Contains("excluded from framework reports", registration.Markup));
        Assert.DoesNotContain("Digital Marketplace", registration.Markup);
        Assert.DoesNotContain("Reporting month", registration.Markup);
        registration.FindAll("input").Single(input => input.ParentElement?.TextContent.Contains("Invoice or credit note number", StringComparison.Ordinal) == true).Input("NF-INVOICE-UI");
        registration.Find("input[type='date']").Change(today.ToString("yyyy-MM-dd"));
        registration.Find("input[type='number']").Change("10000");
        registration.Find(".invoice-command-save").Click();
        var store = context.Services.GetRequiredService<IRemiStore>();
        registration.WaitForAssertion(() => Assert.True(store.ReadAsync(database => database.Invoices.Any(item => item.InvoiceNumber == "NF-INVOICE-UI")).GetAwaiter().GetResult()));
        var invoice = await store.ReadAsync(database => database.Invoices.Single(item => item.InvoiceNumber == "NF-INVOICE-UI"));
        Assert.Equal("", invoice.ReportMonth);
        Assert.Equal(InvoicePaymentStatus.Unpaid, invoice.PaymentStatus);
        var record = context.Render<InvoiceRecordView>(parameters => parameters.Add(component => component.InvoiceId, invoice.Id));
        record.WaitForAssertion(() => Assert.Contains("Payment tracking", record.Markup));
        Assert.DoesNotContain("Digital Marketplace", record.Markup);
        record.Find(".contract-hero-actions button.secondary").Click();
        record.WaitForAssertion(() => Assert.False(record.Find(".invoice-overview-actions button.primary").HasAttribute("disabled")));
        Assert.DoesNotContain("Reporting month", record.Find(".contract-edit-panel").TextContent);
        record.Find(".invoice-overview-actions button.primary").Click();
        record.WaitForAssertion(() => Assert.Empty(record.FindAll(".contract-edit-panel")));
    }

    private static NonFrameworkContractEntry NonFrameworkUiEntry(DateOnly today) => new("NF-UI", "Private Customer", null,
        today.AddYears(-1).AddMonths(-1), null, new(AgreementBasis.Verbal, today.AddYears(-1).AddMonths(-1), "Agreed annual support terms verbally.",
            ContractTermKind.AnnualRolling, 10000, true, 4, "Annual CPI for the preceding December"));
}
