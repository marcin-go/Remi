using Remi.Domain;
using Xunit;

namespace Remi.Tests;

public sealed class MiInvoiceIntakeFormTests
{
    [Fact]
    public void G_cloud_forms_use_the_template_lot_service_group_cascade_and_units()
    {
        var gCloud13 = MiInvoiceIntakeForms.For(FrameworkCode.GCloud13);
        var gCloud14 = MiInvoiceIntakeForms.For(FrameworkCode.GCloud14);

        Assert.Equal(["1", "2", "3"], gCloud13.Lots);
        Assert.Equal(gCloud13.Lots, gCloud14.Lots);
        Assert.Equal(gCloud13.ServiceGroupsFor("1"), gCloud14.ServiceGroupsFor("1"));
        Assert.Equal(
            ["Ongoing Support", "Planning", "Security Services", "Setup and Migration", "Testing", "Training"],
            gCloud14.ServiceGroupsFor("3"));
        Assert.Equal(["Per Unit", "Per User"], gCloud13.UnitOfMeasureOptions);
        Assert.Equal("Per Unit", InvoiceReportingDefaults.UnitOfMeasure);
    }

    [Fact]
    public void Vas_form_has_its_own_lot_product_group_cascade_and_classification()
    {
        var form = MiInvoiceIntakeForms.For(FrameworkCode.VerticalApplicationSolutions);

        Assert.True(form.IsAvailable);
        Assert.Equal(["1", "2", "3", "4", "5"], form.Lots);
        Assert.Contains("Geographic Information System (GIS)", form.ServiceGroupsFor("3"));
        Assert.DoesNotContain("Learning Application", form.ServiceGroupsFor("3"));
        Assert.Equal(["Software", "Hardware", "Associated Service"], form.ServiceGroupLevel2Options);
        Assert.Empty(form.UnitOfMeasureOptions);
    }

    [Fact]
    public void G_cloud_15_form_remains_unavailable_until_its_template_is_published()
    {
        Assert.False(MiInvoiceIntakeForms.For(FrameworkCode.GCloud15).IsAvailable);
        Assert.False(Frameworks.AllowsNewContracts(FrameworkCode.GCloud15));
        Assert.False(ContractReportFields.IsAvailable(FrameworkCode.GCloud15));
    }

    [Fact]
    public void Contract_field_names_match_each_supplied_report_template()
    {
        var gCloud13 = ContractReportFields.For(FrameworkCode.GCloud13);
        var gCloud14 = ContractReportFields.For(FrameworkCode.GCloud14);
        var vas = ContractReportFields.For(FrameworkCode.VerticalApplicationSolutions);

        Assert.Equal("Supplier Reference Number", gCloud13.SupplierReferenceNumber);
        Assert.Equal("Customer Organisation Name", gCloud13.CustomerOrganisationName);
        Assert.Equal("Lot Number", gCloud13.LotNumber);
        Assert.Equal("Supplier reference number", gCloud14.SupplierReferenceNumber);
        Assert.Equal("Customer organisation name", gCloud14.CustomerOrganisationName);
        Assert.Equal("Lot number", gCloud14.LotNumber);
        Assert.Equal("Service Group", gCloud14.ServiceGroup);
        Assert.Equal("Digital Marketplace Service ID", gCloud14.DigitalMarketplaceServiceId);
        Assert.Equal("Product/Service Description", vas.ProductServiceDescription);
        Assert.Equal("Order Channel", vas.OrderChannel);
        Assert.Equal(["Direct Award", "Further Competition"], ContractReportFields.VasOrderChannels);
    }
}
