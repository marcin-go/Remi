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

    [Fact]
    public async Task Gcloud_export_uses_recorded_invoice_values_and_data_row_formatting_for_sparse_template_columns()
    {
        using var template = CreateTemplate(FrameworkCode.GCloud14, includeSparseInvoiceDataRow: true);
        var exporter = new XlsxMiWorkbookExporter();

        var generated = await exporter.GenerateAsync(FrameworkCode.GCloud14, template, [Contract(FrameworkCode.GCloud14)], [Invoice(FrameworkCode.GCloud14)]);

        using var archive = new ZipArchive(generated.Content, ZipArchiveMode.Read, leaveOpen: true);
        var invoiceSheet = ReadXml(archive, "xl/worksheets/sheet2.xml");
        var cells = invoiceSheet.Descendants(SpreadsheetNamespace + "c")
            .Where(cell => ((string?)cell.Attribute("r"))?.EndsWith("2", StringComparison.Ordinal) == true)
            .ToDictionary(cell => (string)cell.Attribute("r")!, cell => cell, StringComparer.Ordinal);
        Assert.Equal("each", CellValue(cells["I2"]));
        Assert.Equal("1", CellValue(cells["J2"]));
        Assert.Equal("1000", CellValue(cells["K2"]));
        Assert.Equal("1000", CellValue(cells["L2"]));
        Assert.All(["H2", "I2", "J2", "K2", "L2"], reference => Assert.Equal("3", (string?)cells[reference].Attribute("s")));
    }

    [Theory]
    [InlineData(FrameworkCode.GCloud14)]
    [InlineData(FrameworkCode.VerticalApplicationSolutions)]
    public async Task Export_writes_contract_and_invoice_dates_as_required_text(FrameworkCode framework)
    {
        using var template = CreateTemplate(framework);
        var exporter = new XlsxMiWorkbookExporter();

        var generated = await exporter.GenerateAsync(framework, template, [Contract(framework)], [Invoice(framework)]);

        using var archive = new ZipArchive(generated.Content, ZipArchiveMode.Read, leaveOpen: true);
        var contractSheet = ReadXml(archive, "xl/worksheets/sheet1.xml");
        var invoiceSheet = ReadXml(archive, "xl/worksheets/sheet2.xml");

        AssertDateText(contractSheet, ContractHeaders(framework), "Contract start date", "01/07/2026");
        AssertDateText(contractSheet, ContractHeaders(framework), "Contract end date", "31/07/2026");
        AssertDateText(invoiceSheet, InvoiceHeaders(framework), "Customer invoice credit note date", "31/07/2026");
    }

    private static MemoryStream CreateTemplate(FrameworkCode framework, bool includeSparseInvoiceDataRow = false)
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
            WriteXml(archive, "xl/worksheets/sheet2.xml", Sheet(InvoiceHeaders(framework), includeSparseInvoiceDataRow));
        }

        output.Position = 0;
        return output;
    }

    private static XDocument Sheet(IReadOnlyList<string> headers, bool includeSparseDataRow = false)
    {
        var rows = new List<XElement>
        {
            new(SpreadsheetNamespace + "row", new XAttribute("r", "1"), headers.Select((header, index) =>
                new XElement(SpreadsheetNamespace + "c",
                    new XAttribute("s", "5"),
                    new XAttribute("r", $"{ColumnName(index)}1"),
                    new XAttribute("t", "inlineStr"),
                    new XElement(SpreadsheetNamespace + "is", new XElement(SpreadsheetNamespace + "t", header))))),
        };
        if (includeSparseDataRow)
        {
            rows.Add(new XElement(SpreadsheetNamespace + "row", new XAttribute("r", "2"), Enumerable.Range(0, 7).Select(index =>
                new XElement(SpreadsheetNamespace + "c", new XAttribute("r", $"{ColumnName(index)}2"), new XAttribute("s", index == 3 ? "4" : "3")))));
        }

        return new XDocument(new XElement(SpreadsheetNamespace + "worksheet", new XElement(SpreadsheetNamespace + "sheetData", rows)));
    }

    private static IReadOnlyList<string> ContractHeaders(FrameworkCode framework) =>
        framework == FrameworkCode.VerticalApplicationSolutions
            ? ["Supplier reference number", "Customer organisation name", "Contract start date", "Contract end date", "Total contract value", "Product service description", "Order channel"]
            : ["Supplier reference number", "Customer organisation name", "Contract start date", "Contract end date", "Total contract value", "Service group", "Digital Marketplace service ID"];

    private static IReadOnlyList<string> InvoiceHeaders(FrameworkCode framework) =>
        framework == FrameworkCode.VerticalApplicationSolutions
            ? ["Supplier reference number", "Customer organisation name", "Customer invoice credit note date", "Customer invoice credit note number", "Total cost ex VAT", "Product service description"]
            : ["Supplier reference number", "Customer Unique Reference Number (URN)", "Customer organisation name", "Customer invoice credit note date", "Customer invoice credit note number", "Lot number", "Service Group", "Digital Marketplace service ID", "Unit of measure", "Quantity", "Price per Unit", "Total cost ex VAT"];

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

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        using var stream = archive.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }

    private static string CellValue(XElement cell) =>
        (string?)cell.Element(SpreadsheetNamespace + "v") ??
        (string?)cell.Element(SpreadsheetNamespace + "is")?.Element(SpreadsheetNamespace + "t") ??
        string.Empty;

    private static void AssertDateText(XDocument sheet, IReadOnlyList<string> headers, string header, string expected)
    {
        var column = ColumnName(headers.ToList().IndexOf(header));
        var cell = sheet.Descendants(SpreadsheetNamespace + "c").Single(item => (string?)item.Attribute("r") == $"{column}2");
        Assert.Equal("inlineStr", (string?)cell.Attribute("t"));
        Assert.Equal(expected, CellValue(cell));
    }
}
