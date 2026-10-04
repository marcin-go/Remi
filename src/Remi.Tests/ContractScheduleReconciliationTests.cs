using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Remi.Application;
using Remi.Domain;
using Remi.Web.Components;
using Xunit;

namespace Remi.Tests;

public sealed partial class RegisterComponentTests
{
    [Theory]
    [InlineData(0, 0, false, "Needs reconciliation", 100)]
    [InlineData(40, 0, false, "Partially reconciled", 60)]
    [InlineData(100, 0, false, "Reconciled", 0)]
    [InlineData(100, -30, false, "Partially reconciled", 30)]
    [InlineData(0, 0, true, "Optional · agreement needed", 100)]
    public async Task Contract_schedule_shows_net_reconciliation_for_each_position(
        int allocated, int credit, bool optional, string expectedState, int outstanding)
    {
        using var context = CreateContext(invoiceFramework: FrameworkCode.GCloud14);
        var positionId = Guid.NewGuid();
        await context.Services.GetRequiredService<IRemiStore>().UpdateAsync(database =>
        {
            database.ChargeScheduleItems.Add(new(positionId, SampleContractId, null, 1,
                "Licence", new(2026, 4, 1), 100, optional, DateTimeOffset.UtcNow));
            database.ChargeScheduleItems.Add(new(Guid.NewGuid(), SampleContractId, null, 1,
                "Training", new(2026, 4, 1), 50, false, DateTimeOffset.UtcNow));
            if (allocated != 0)
                database.InvoicePaymentAllocations.Add(new(SampleInvoiceId, positionId,
                    BillingPositionSource.ChargeSchedule, allocated, DateTimeOffset.UtcNow));
            if (credit != 0)
            {
                var creditInvoice = database.Invoices[0] with { Id = Guid.NewGuid(), InvoiceNumber = "CREDIT", TotalCostExVat = credit };
                database.Invoices.Add(creditInvoice);
                database.InvoicePaymentAllocations.Add(new(creditInvoice.Id, positionId,
                    BillingPositionSource.ChargeSchedule, credit, DateTimeOffset.UtcNow));
            }
            return true;
        });
        var cut = context.Render<ContractRecordView>(parameters => parameters.Add(item => item.ContractId, SampleContractId));
        cut.WaitForAssertion(() => Assert.Contains("Reconciliation", cut.Find(".contract-payment-schedule thead").TextContent));
        var rows = cut.FindAll(".contract-payment-schedule tbody tr");
        var licence = rows.Single(row => row.Children[1].TextContent == "Licence");
        Assert.Equal(expectedState, licence.QuerySelector(".register-status")!.TextContent.Trim());
        Assert.Equal(outstanding == 0, licence.QuerySelector(".register-status")!.ClassList.Contains("good"));
        if (allocated != 0)
        {
            Assert.Contains($"£{allocated + credit:N2} allocated", licence.TextContent);
            if (outstanding > 0) Assert.Contains($"£{outstanding:N2} outstanding", licence.TextContent);
            else Assert.DoesNotContain("outstanding", licence.TextContent);
        }
        Assert.Contains("Needs reconciliation", rows.Single(row => row.Children[1].TextContent == "Training").TextContent);
        var total = rows.Single(row => row.ClassList.Contains("contract-payment-total"));
        Assert.Equal("£150.00", total.Children[1].TextContent);
        Assert.Equal(3, total.Children.Length);
    }

    [Fact]
    public async Task Contract_schedule_reopens_after_an_invoice_allocation_is_removed()
    {
        using var context = CreateContext(invoiceFramework: FrameworkCode.GCloud14);
        var positionId = Guid.NewGuid();
        await context.Services.GetRequiredService<IRemiStore>().UpdateAsync(database =>
        {
            database.ChargeScheduleItems.Add(new(positionId, SampleContractId, null, 1,
                "Licence", new(2026, 4, 1), 100, false, DateTimeOffset.UtcNow));
            return true;
        });
        var workspace = context.Services.GetRequiredService<ReportingWorkspace>();
        Assert.True((await workspace.AllocateInvoicePaymentAsync(SampleInvoiceId, positionId, BillingPositionSource.ChargeSchedule, 100)).Succeeded);
        using (var reconciled = context.Render<ContractRecordView>(parameters => parameters.Add(item => item.ContractId, SampleContractId)))
            reconciled.WaitForAssertion(() => Assert.Equal("Reconciled", reconciled.Find(".contract-payment-schedule .register-status").TextContent.Trim()));
        Assert.True((await workspace.RemoveInvoicePaymentAllocationAsync(SampleInvoiceId, positionId, BillingPositionSource.ChargeSchedule)).Succeeded);
        using var reopened = context.Render<ContractRecordView>(parameters => parameters.Add(item => item.ContractId, SampleContractId));
        reopened.WaitForAssertion(() => Assert.Equal("Needs reconciliation", reopened.Find(".contract-payment-schedule .register-status").TextContent.Trim()));
    }
}
