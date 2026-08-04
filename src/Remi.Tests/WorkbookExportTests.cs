using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class WorkbookExportTests
{
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    [Theory]
    [InlineData(FrameworkCode.GCloud14)]
    [InlineData(FrameworkCode.VerticalApplicationSolutions)]
    public async Task Representative_framework_export_retains_a_valid_template_structure(FrameworkCode framework)
    {
        using var template = CreateTemplate(framework);
        var exporter = new XlsxMiWorkbookExporter();

        var validation = await exporter.ValidateTemplateAsync(framework, template);

        Assert.True(validation.IsValid);
        template.Position = 0;
        var generated = await exporter.GenerateAsync(framework, template, [Contract(framework)], [Invoice(framework)]);

        Assert.DoesNotContain(generated.Findings, finding => finding.Severity == FindingSeverity.Error);
        Assert.True(generated.Content.Length > 0);
        using var archive = new ZipArchive(generated.Content, ZipArchiveMode.Read, leaveOpen: true);
        Assert.NotNull(archive.GetEntry("xl/worksheets/sheet1.xml"));
        Assert.NotNull(archive.GetEntry("xl/worksheets/sheet2.xml"));
    }

    private static MemoryStream CreateTemplate(FrameworkCode framework)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteXml(archive, "xl/workbook.xml", new XDocument(
                new XElement(SpreadsheetNamespace + "workbook",
                    new XAttribute(XNamespace.Xmlns + "r", RelationshipNamespace.NamespaceName),
                    new XElement(SpreadsheetNamespace + "sheets",
                        new XElement(SpreadsheetNamespace + "sheet", new XAttribute("name", "Contracts"), new XAttribute("sheetId", "1"), new XAttribute(RelationshipNamespace + "id", "rId1")),
                        new XElement(SpreadsheetNamespace + "sheet", new XAttribute("name", "Invoices Raised"), new XAttribute("sheetId", "2"), new XAttribute(RelationshipNamespace + "id", "rId2"))))));
            WriteXml(archive, "xl/_rels/workbook.xml.rels", new XDocument(
                new XElement(PackageRelationshipNamespace + "Relationships",
                    new XElement(PackageRelationshipNamespace + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml")),
                    new XElement(PackageRelationshipNamespace + "Relationship", new XAttribute("Id", "rId2"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), new XAttribute("Target", "worksheets/sheet2.xml")))));
            WriteXml(archive, "xl/worksheets/sheet1.xml", Sheet(ContractHeaders(framework)));
            WriteXml(archive, "xl/worksheets/sheet2.xml", Sheet(InvoiceHeaders(framework)));
        }

        output.Position = 0;
        return output;
    }

    private static XDocument Sheet(IReadOnlyList<string> headers) => new(
        new XElement(SpreadsheetNamespace + "worksheet",
            new XElement(SpreadsheetNamespace + "sheetData",
                new XElement(SpreadsheetNamespace + "row", new XAttribute("r", "1"), headers.Select((header, index) =>
                    new XElement(SpreadsheetNamespace + "c",
                        new XAttribute("r", $"{ColumnName(index)}1"),
                        new XAttribute("t", "inlineStr"),
                        new XElement(SpreadsheetNamespace + "is", new XElement(SpreadsheetNamespace + "t", header))))))));

    private static IReadOnlyList<string> ContractHeaders(FrameworkCode framework) =>
        framework == FrameworkCode.VerticalApplicationSolutions
            ? ["Supplier reference number", "Customer organisation name", "Total contract value", "Product service description", "Order channel"]
            : ["Supplier reference number", "Customer organisation name", "Total contract value", "Service group", "Digital Marketplace service ID"];

    private static IReadOnlyList<string> InvoiceHeaders(FrameworkCode framework) =>
        framework == FrameworkCode.VerticalApplicationSolutions
            ? ["Supplier reference number", "Customer organisation name", "Customer invoice credit note number", "Total cost ex VAT", "Product service description"]
            : ["Supplier reference number", "Customer organisation name", "Customer invoice credit note number", "Total cost ex VAT", "Service group", "Digital Marketplace service ID", "Unit of measure"];

    private static ContractRecord Contract(FrameworkCode framework) => new(
        Guid.NewGuid(), framework, "RM-001", "Example customer", "URN-001", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), "Lot 1", "Cloud support", null, "Managed reporting service", "Direct award", "123456", 1000, "2026-07", "test.xlsx", DateTimeOffset.UtcNow);

    private static InvoiceRecord Invoice(FrameworkCode framework) => new(
        Guid.NewGuid(), framework, "RM-001", "Example customer", "URN-001", new DateOnly(2026, 7, 31), "INV-001", "Lot 1", "Cloud support", null, "Managed reporting service", "Direct award", "123456", "each", 1, 1000, 1000, null, null, "2026-07", "test.xlsx", DateTimeOffset.UtcNow);

    private static string ColumnName(int zeroBasedIndex)
    {
        var index = zeroBasedIndex + 1;
        var name = string.Empty;
        while (index > 0)
        {
            index--;
            name = (char)('A' + (index % 26)) + name;
            index /= 26;
        }

        return name;
    }

    private static void WriteXml(ZipArchive archive, string path, XDocument document)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        document.Save(writer);
    }
}
