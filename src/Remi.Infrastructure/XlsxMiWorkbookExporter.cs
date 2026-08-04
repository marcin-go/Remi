using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

/// <summary>
/// Copies an approved workbook package, replaces only the data rows in its Contracts and
/// Invoices Raised tables, and leaves its guidance, lookups, styles and validation intact.
/// </summary>
public sealed class XlsxMiWorkbookExporter : IMiWorkbookExporter
{
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace XmlNamespace = XNamespace.Xml;

    public async Task<TemplateValidationResult> ValidateTemplateAsync(
        FrameworkCode framework,
        Stream workbook,
        CancellationToken cancellationToken = default)
    {
        await using var copy = await CopyAsync(workbook, cancellationToken);
        using var archive = new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: true);
        var description = DescribeWorkbook(archive);
        var findings = ValidateDescription(framework, description);
        return new TemplateValidationResult(!findings.Any(finding => finding.Severity == FindingSeverity.Error), findings);
    }

    public async Task<GeneratedMiWorkbook> GenerateAsync(
        FrameworkCode framework,
        Stream templateWorkbook,
        IReadOnlyList<ContractRecord> contracts,
        IReadOnlyList<InvoiceRecord> invoices,
        CancellationToken cancellationToken = default)
    {
        await using var copiedTemplate = await CopyAsync(templateWorkbook, cancellationToken);
        using var sourceArchive = new ZipArchive(copiedTemplate, ZipArchiveMode.Read, leaveOpen: true);
        var description = DescribeWorkbook(sourceArchive);
        var findings = ValidateDescription(framework, description).ToList();
        if (findings.Any(finding => finding.Severity == FindingSeverity.Error))
        {
            throw new InvalidDataException("The approved workbook does not match the expected MI template structure.");
        }

        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var contractSheet = description.GetSheet("Contracts");
        var invoiceSheet = description.GetSheet("Invoices Raised");
        var contractRows = contracts.Select(ToContractRow).ToList();
        var invoiceRows = invoices.Select(ToInvoiceRow).ToList();
        PrepareSheet(sourceArchive, description, contractSheet, contractRows, replacements);
        PrepareSheet(sourceArchive, description, invoiceSheet, invoiceRows, replacements);

        var output = new MemoryStream();
        using (var destinationArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in sourceArchive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationEntry = destinationArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                await using var destination = destinationEntry.Open();
                if (replacements.TryGetValue(entry.FullName, out var replacement))
                {
                    await destination.WriteAsync(replacement, cancellationToken);
                    continue;
                }

                await using var source = entry.Open();
                await source.CopyToAsync(destination, cancellationToken);
            }
        }

        output.Position = 0;
        using (var outputArchive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
        {
            var generatedDescription = DescribeWorkbook(outputArchive);
            VerifyGeneratedRows(generatedDescription.GetSheet("Contracts"), contractRows.Count, findings);
            VerifyGeneratedRows(generatedDescription.GetSheet("Invoices Raised"), invoiceRows.Count, findings);
        }

        if (findings.Any(finding => finding.Severity == FindingSeverity.Error))
        {
            throw new InvalidDataException("The generated workbook did not retain the expected MI table structure.");
        }

        output.Position = 0;
        return new GeneratedMiWorkbook(output, findings);
    }

    private static async Task<MemoryStream> CopyAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new MemoryStream();
        await source.CopyToAsync(copy, cancellationToken);
        copy.Position = 0;
        return copy;
    }

    private static WorkbookDescription DescribeWorkbook(ZipArchive archive)
    {
        var sharedStrings = ReadSharedStrings(archive);
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var relationships = ReadRelationships(archive, "xl/_rels/workbook.xml.rels", "xl/workbook.xml");
        var sheets = new List<WorksheetDescription>();
        foreach (var sheet in workbook.Descendants(SpreadsheetNamespace + "sheet"))
        {
            var name = (string?)sheet.Attribute("name");
            var relationshipId = (string?)sheet.Attribute(RelationshipNamespace + "id");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relationshipId) || !relationships.TryGetValue(relationshipId, out var path))
            {
                continue;
            }

            var document = LoadXml(archive, path);
            var rows = document.Descendants(SpreadsheetNamespace + "row")
                .Select(row => ReadRow(row, sharedStrings))
                .Where(row => row.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
                .ToList();
            var header = rows.FirstOrDefault() ?? throw new InvalidDataException($"The {name} sheet has no header row.");
            sheets.Add(new WorksheetDescription(name, path, document, header.RowNumber, header.Values, rows));
        }

        return new WorkbookDescription(sharedStrings, sheets);
    }

    private static IReadOnlyList<ValidationFinding> ValidateDescription(FrameworkCode framework, WorkbookDescription description)
    {
        var findings = new List<ValidationFinding>();
        var contractSheet = description.TryGetSheet("Contracts");
        var invoiceSheet = description.TryGetSheet("Invoices Raised");
        if (contractSheet is null)
        {
            findings.Add(Error("TemplateContractsSheetMissing", "The approved workbook must contain a Contracts sheet."));
        }

        if (invoiceSheet is null)
        {
            findings.Add(Error("TemplateInvoicesSheetMissing", "The approved workbook must contain an Invoices Raised sheet."));
        }

        if (contractSheet is not null)
        {
            ValidateHeaders(contractSheet, ["supplierreferencenumber", "customerorganisationname", "totalcontractvalue"], "Contracts", findings);
            if (framework == FrameworkCode.VerticalApplicationSolutions)
            {
                ValidateHeaders(contractSheet, ["productservicedescription", "orderchannel"], "Contracts", findings);
            }
            else
            {
                ValidateHeaders(contractSheet, ["servicegroup", "digitalmarketplaceserviceid"], "Contracts", findings);
            }
        }

        if (invoiceSheet is not null)
        {
            ValidateHeaders(invoiceSheet, ["supplierreferencenumber", "customerorganisationname", "customerinvoicecreditnotenumber", "totalcostexvat"], "Invoices Raised", findings);
            if (framework == FrameworkCode.VerticalApplicationSolutions)
            {
                ValidateHeaders(invoiceSheet, ["productservicedescription"], "Invoices Raised", findings);
            }
            else
            {
                ValidateHeaders(invoiceSheet, ["servicegroup", "digitalmarketplaceserviceid", "unitofmeasure"], "Invoices Raised", findings);
            }
        }

        return findings;
    }

    private static void ValidateHeaders(
        WorksheetDescription sheet,
        IEnumerable<string> requiredHeaders,
        string displayName,
        ICollection<ValidationFinding> findings)
    {
        var headers = sheet.Headers.Values.Select(NormaliseHeader).ToHashSet(StringComparer.Ordinal);
        foreach (var header in requiredHeaders.Where(header => !headers.Contains(header)))
        {
            findings.Add(Error("TemplateColumnMissing", $"The {displayName} sheet is missing the required {header} column."));
        }
    }

    private static void PrepareSheet(
        ZipArchive archive,
        WorkbookDescription workbook,
        WorksheetDescription sheet,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> values,
        IDictionary<string, byte[]> replacements)
    {
        var sheetDocument = new XDocument(sheet.Document);
        var sheetData = sheetDocument.Root?.Element(SpreadsheetNamespace + "sheetData")
            ?? throw new InvalidDataException($"The {sheet.Name} sheet has no sheet data.");
        var headerRow = sheetData.Elements(SpreadsheetNamespace + "row")
            .SingleOrDefault(row => RowNumber(row) == sheet.HeaderRow)
            ?? throw new InvalidDataException($"The {sheet.Name} sheet header could not be found.");
        var templateRow = sheetData.Elements(SpreadsheetNamespace + "row")
            .FirstOrDefault(row => RowNumber(row) > sheet.HeaderRow);
        sheetData.Elements(SpreadsheetNamespace + "row")
            .Where(row => RowNumber(row) > sheet.HeaderRow)
            .Remove();

        var cellsByColumn = headerRow.Elements(SpreadsheetNamespace + "c")
            .ToDictionary(cell => ColumnName((string?)cell.Attribute("r") ?? string.Empty), cell => cell, StringComparer.Ordinal);
        var templateCells = templateRow?.Elements(SpreadsheetNamespace + "c")
            .ToDictionary(cell => ColumnName((string?)cell.Attribute("r") ?? string.Empty), cell => cell, StringComparer.Ordinal)
            ?? new Dictionary<string, XElement>(StringComparer.Ordinal);
        var headersByColumn = sheet.Headers.ToDictionary(pair => NormaliseHeader(pair.Value), pair => pair.Key, StringComparer.Ordinal);
        for (var rowIndex = 0; rowIndex < values.Count; rowIndex++)
        {
            var excelRow = sheet.HeaderRow + rowIndex + 1;
            var row = new XElement(SpreadsheetNamespace + "row", new XAttribute("r", excelRow));
            foreach (var header in headersByColumn.OrderBy(pair => ColumnIndex(pair.Value)))
            {
                var column = header.Value;
                var sourceCell = templateCells.GetValueOrDefault(column) ?? cellsByColumn.GetValueOrDefault(column);
                var cell = sourceCell is null
                    ? new XElement(SpreadsheetNamespace + "c")
                    : new XElement(sourceCell);
                cell.SetAttributeValue("r", $"{column}{excelRow}");
                WriteCellValue(cell, values[rowIndex].GetValueOrDefault(header.Key));
                row.Add(cell);
            }

            sheetData.Add(row);
        }

        replacements[sheet.Path] = Serialize(sheetDocument);
        UpdateTableReferences(archive, sheet, values.Count, replacements);
    }

    private static void UpdateTableReferences(
        ZipArchive archive,
        WorksheetDescription sheet,
        int dataRowCount,
        IDictionary<string, byte[]> replacements)
    {
        var relationshipPath = RelationshipPathFor(sheet.Path);
        var relationships = ReadRelationships(archive, relationshipPath, sheet.Path, missingIsEmpty: true);
        foreach (var tableId in sheet.Document.Descendants(SpreadsheetNamespace + "tablePart")
                     .Select(part => (string?)part.Attribute(RelationshipNamespace + "id"))
                     .Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (!relationships.TryGetValue(tableId!, out var tablePath))
            {
                continue;
            }

            var table = LoadXml(archive, tablePath);
            var reference = (string?)table.Root?.Attribute("ref");
            if (string.IsNullOrWhiteSpace(reference) || !TryParseRange(reference, out var firstColumn, out var _, out var lastColumn, out var _))
            {
                continue;
            }

            var updatedReference = $"{firstColumn}{sheet.HeaderRow}:{lastColumn}{sheet.HeaderRow + Math.Max(dataRowCount, 1)}";
            table.Root!.SetAttributeValue("ref", updatedReference);
            table.Root.Element(SpreadsheetNamespace + "autoFilter")?.SetAttributeValue("ref", updatedReference);
            replacements[tablePath] = Serialize(table);
        }
    }

    private static void VerifyGeneratedRows(WorksheetDescription sheet, int expectedRows, ICollection<ValidationFinding> findings)
    {
        var actualRows = sheet.Rows.Count(row => row.RowNumber > sheet.HeaderRow && row.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value)));
        if (actualRows != expectedRows)
        {
            findings.Add(Error("GeneratedRowCountMismatch", $"The generated {sheet.Name} sheet has {actualRows} data row(s); expected {expectedRows}."));
        }
    }

    private static IReadOnlyDictionary<string, object?> ToContractRow(ContractRecord contract) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["supplierreferencenumber"] = contract.SupplierReference,
        ["customeruniquereferencenumberurn"] = contract.CustomerUrn,
        ["customerorganisationname"] = contract.CustomerName,
        ["contractstartdate"] = contract.StartDate,
        ["contractenddate"] = contract.EndDate,
        ["lotnumber"] = contract.LotNumber,
        ["servicegroup"] = contract.ServiceGroup,
        ["productservicegrouplevel1"] = contract.ServiceGroup,
        ["productservicegrouplevel2"] = contract.ServiceGroupLevel2,
        ["productservicedescription"] = contract.ServiceDescription,
        ["orderchannel"] = contract.OrderChannel,
        ["digitalmarketplaceserviceid"] = contract.DigitalMarketplaceServiceId,
        ["totalcontractvalue"] = contract.TotalContractValueExVat,
    };

    private static IReadOnlyDictionary<string, object?> ToInvoiceRow(InvoiceRecord invoice) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["supplierreferencenumber"] = invoice.SupplierReference,
        ["customeruniquereferencenumberurn"] = invoice.CustomerUrn,
        ["customerorganisationname"] = invoice.CustomerName,
        ["customerinvoicecreditnotedate"] = invoice.InvoiceDate,
        ["customerinvoicecreditnotenumber"] = invoice.InvoiceNumber,
        ["lotnumber"] = invoice.LotNumber,
        ["servicegroup"] = invoice.ServiceGroup,
        ["productservicegrouplevel1"] = invoice.ServiceGroup,
        ["productservicegrouplevel2"] = invoice.ServiceGroupLevel2,
        ["productservicedescription"] = invoice.ServiceDescription,
        ["orderchannel"] = invoice.OrderChannel,
        ["digitalmarketplaceserviceid"] = invoice.DigitalMarketplaceServiceId,
        ["unitofmeasure"] = invoice.UnitOfMeasure,
        ["quantity"] = invoice.Quantity,
        ["priceperunit"] = invoice.PricePerUnitExVat,
        ["totalcostexvat"] = invoice.TotalCostExVat,
        ["originalvendor"] = invoice.OriginalVendor,
        ["subcontractorname"] = invoice.SubcontractorName,
    };

    private static void WriteCellValue(XElement cell, object? value)
    {
        cell.Elements(SpreadsheetNamespace + "v").Remove();
        cell.Elements(SpreadsheetNamespace + "is").Remove();
        cell.Elements(SpreadsheetNamespace + "f").Remove();
        if (value is null)
        {
            cell.SetAttributeValue("t", null);
            return;
        }

        switch (value)
        {
            case DateOnly date:
                cell.SetAttributeValue("t", null);
                cell.Add(new XElement(SpreadsheetNamespace + "v", date.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture)));
                break;
            case decimal number:
                cell.SetAttributeValue("t", null);
                cell.Add(new XElement(SpreadsheetNamespace + "v", number.ToString(CultureInfo.InvariantCulture)));
                break;
            case int integer:
                cell.SetAttributeValue("t", null);
                cell.Add(new XElement(SpreadsheetNamespace + "v", integer.ToString(CultureInfo.InvariantCulture)));
                break;
            default:
                cell.SetAttributeValue("t", "inlineStr");
                var text = new XElement(SpreadsheetNamespace + "t", Convert.ToString(value, CultureInfo.InvariantCulture));
                if ((text.Value ?? string.Empty).StartsWith(' ') || (text.Value ?? string.Empty).EndsWith(' '))
                {
                    text.SetAttributeValue(XmlNamespace + "space", "preserve");
                }

                cell.Add(new XElement(SpreadsheetNamespace + "is", text));
                break;
        }
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive archive, string relationshipPath, string sourcePart, bool missingIsEmpty = false)
    {
        var entry = archive.GetEntry(relationshipPath);
        if (entry is null)
        {
            return missingIsEmpty
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : throw new InvalidDataException($"The workbook is missing {relationshipPath}.");
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants(PackageRelationshipNamespace + "Relationship")
            .Where(relationship => !string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                relationship => (string?)relationship.Attribute("Id") ?? string.Empty,
                relationship => ResolvePartPath(sourcePart, (string?)relationship.Attribute("Target") ?? string.Empty),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants(SpreadsheetNamespace + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value)))
            .ToList();
    }

    private static SheetRow ReadRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in row.Elements(SpreadsheetNamespace + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? string.Empty;
            values[ColumnName(reference)] = ReadCell(cell, sharedStrings);
        }

        return new SheetRow(RowNumber(row), values);
    }

    private static string ReadCell(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            return string.Concat(cell.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value));
        }

        var value = cell.Element(SpreadsheetNamespace + "v")?.Value ?? string.Empty;
        return string.Equals(type, "s", StringComparison.Ordinal) && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count
            ? sharedStrings[index]
            : value;
    }

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"The workbook is missing {path}.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static byte[] Serialize(XDocument document)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = false }))
        {
            document.Save(writer);
        }

        return output.ToArray();
    }

    private static string RelationshipPathFor(string partPath) =>
        $"{Path.GetDirectoryName(partPath)?.Replace('\\', '/')}/_rels/{Path.GetFileName(partPath)}.rels";

    private static string ResolvePartPath(string sourcePart, string target)
    {
        var baseUri = new Uri($"https://package/{sourcePart}", UriKind.Absolute);
        var resolved = new Uri(baseUri, target);
        return resolved.AbsolutePath.TrimStart('/');
    }

    private static int RowNumber(XElement row) => int.TryParse((string?)row.Attribute("r"), out var number) ? number : 0;

    private static string ColumnName(string reference) => new(reference.TakeWhile(char.IsLetter).Select(char.ToUpperInvariant).ToArray());

    private static int ColumnIndex(string column)
    {
        var index = 0;
        foreach (var character in column)
        {
            index = (index * 26) + (character - 'A' + 1);
        }

        return index;
    }

    private static string NormaliseHeader(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryParseRange(string value, out string firstColumn, out int firstRow, out string lastColumn, out int lastRow)
    {
        var pieces = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length != 2)
        {
            firstColumn = lastColumn = string.Empty;
            firstRow = lastRow = 0;
            return false;
        }

        firstColumn = ColumnName(pieces[0]);
        lastColumn = ColumnName(pieces[1]);
        firstRow = ParseRowNumber(pieces[0]);
        lastRow = ParseRowNumber(pieces[1]);
        return firstColumn.Length != 0 && lastColumn.Length != 0 && firstRow > 0 && lastRow > 0;
    }

    private static int ParseRowNumber(string reference) => int.TryParse(new string(reference.SkipWhile(char.IsLetter).ToArray()), out var number) ? number : 0;

    private static ValidationFinding Error(string code, string message) =>
        new(FindingSeverity.Error, code, message, "Template");

    private sealed record WorkbookDescription(IReadOnlyList<string> SharedStrings, IReadOnlyList<WorksheetDescription> Sheets)
    {
        public WorksheetDescription GetSheet(string name) => TryGetSheet(name)
            ?? throw new InvalidDataException($"The workbook does not contain a {name} sheet.");

        public WorksheetDescription? TryGetSheet(string name) => Sheets.SingleOrDefault(sheet => string.Equals(sheet.Name.Replace(" ", string.Empty, StringComparison.Ordinal), name.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
    }

    private sealed record WorksheetDescription(
        string Name,
        string Path,
        XDocument Document,
        int HeaderRow,
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyList<SheetRow> Rows);

    private sealed record SheetRow(int RowNumber, IReadOnlyDictionary<string, string> Values);
}
