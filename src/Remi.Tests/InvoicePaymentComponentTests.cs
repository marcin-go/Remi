using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web.Components;
using Xunit;

namespace Remi.Tests;

public sealed partial class RegisterComponentTests
{
    [Fact]
    public async Task Invoice_payment_controls_clear_assumed_dates_and_can_record_a_reviewed_payment()
    {
        using var context = CreateContext();
        var store = context.Services.GetRequiredService<IRemiStore>();
        var today = ContractPortfolioRules.Today(context.Services.GetRequiredService<TimeProvider>());
        await store.UpdateAsync(database =>
        {
            database.Invoices[0] = database.Invoices[0] with { InvoiceDate = today.AddDays(-40), PaymentStatus = InvoicePaymentStatus.Paid, DatePaid = today.AddDays(-25), IsPaymentDateAssumed = true };
            return true;
        });
        var cut = context.Render<InvoiceRecordView>(parameters => parameters.Add(component => component.InvoiceId, SampleInvoiceId));
        cut.WaitForAssertion(() => Assert.Contains("assumed", cut.Find(".invoice-payment-tracking").TextContent));
        cut.FindAll(".invoice-payment-tracking button").Single(button => button.TextContent.StartsWith("Mark unpaid", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("10 days overdue", cut.Find(".invoice-payment-tracking").TextContent);
            Assert.True(string.IsNullOrEmpty(cut.Find("input[aria-label='Date paid']").GetAttribute("value")));
            Assert.True(cut.Find("input[aria-label='Date paid']").HasAttribute("disabled"));
        });
        Assert.Null(await store.ReadAsync(database => database.Invoices[0].DatePaid));
        cut.Find("button[aria-label='Payment status']").Click();
        cut.FindAll("button[role='option']").Single(button => button.TextContent.Trim() == "Paid").Click();
        cut.Find("input[aria-label='Date paid']").Change(today.ToString("yyyy-MM-dd"));
        cut.Find(".invoice-payment-tracking button.remi-action--primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Payment details saved", cut.Find(".invoice-payment-tracking [role='status']").TextContent));
        Assert.Equal(today, await store.ReadAsync(database => database.Invoices[0].DatePaid));
        Assert.False(await store.ReadAsync(database => database.Invoices[0].IsPaymentDateAssumed));
    }

    [Fact]
    public async Task Payment_reports_can_switch_between_unpaid_overdue_and_unknown_and_export_same_filter()
    {
        using var context = CreateContext();
        var store = context.Services.GetRequiredService<IRemiStore>();
        var today = ContractPortfolioRules.Today(context.Services.GetRequiredService<TimeProvider>());
        await store.UpdateAsync(database =>
        {
            database.Invoices[0] = database.Invoices[0] with { InvoiceDate = today.AddDays(-40), PaymentStatus = InvoicePaymentStatus.Unpaid };
            database.Invoices.Add(database.Invoices[0] with { Id = Guid.NewGuid(), InvoiceNumber = "UNKNOWN-1", PaymentStatus = InvoicePaymentStatus.Unknown });
            database.Invoices.Add(database.Invoices[0] with { Id = Guid.NewGuid(), InvoiceNumber = "DUE-TODAY", InvoiceDate = today.AddDays(-30) });
            return true;
        });
        var cut = context.Render<InvoicePaymentReports>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));
        cut.Find("button[aria-controls='payment-report-status']").Click();
        cut.FindAll("button[role='option']").Single(button => button.TextContent.Trim() == "Overdue").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("tbody tr"));
            Assert.Contains("status=overdue", cut.Find("a[href^='/invoices/payments.csv']").GetAttribute("href"));
            Assert.DoesNotContain("UNKNOWN-1", cut.Markup);
            Assert.DoesNotContain("DUE-TODAY", cut.Markup);
        });
        cut.Find("button[aria-controls='payment-report-status']").Click();
        cut.FindAll("button[role='option']").Single(button => button.TextContent.Trim() == "Unknown · needs review").Click();
        cut.WaitForAssertion(() => Assert.Contains("UNKNOWN-1", cut.Find("tbody").TextContent));
        Assert.Contains("status=unknown", cut.Find("a[href^='/invoices/payments.csv']").GetAttribute("href"));
    }
}
