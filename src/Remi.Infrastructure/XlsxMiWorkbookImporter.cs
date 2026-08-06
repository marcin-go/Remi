using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

public sealed class XlsxMiWorkbookImporter : IWorkbookImporter
{
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    public async Task<ImportedWorkbook> ImportAsync(
        FrameworkCode framework,
        string workbookName,
        Stream workbook,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookName);
        ArgumentNullException.ThrowIfNull(workbook);

        await using var copy = new MemoryStream();
        await workbook.CopyToAsync(copy, cancellationToken);
        copy.Position = 0;

        using var archive = new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: true);
        var sharedStrings = ReadSharedStrings(archive);
        var sheets = ReadSheets(archive, sharedStrings);
        if (!sheets.TryGetValue("Contracts", out var contractsSheet))
        {
            throw new InvalidDataException("The workbook does not contain a Contracts sheet.");
        }

        var invoiceSheet = sheets
            .FirstOrDefault(pair => string.Equals(pair.Key.Replace(" ", string.Empty, StringComparison.Ordinal), "InvoicesRaised", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(invoiceSheet.Key))
        {
            throw new InvalidDataException("The workbook does not contain an Invoices Raised sheet.");
        }

        var contracts = contractsSheet.Select(row => ToContract(framework, row)).ToList();
        var invoices = invoiceSheet.Value.Select(row => ToInvoice(framework, row)).ToList();
        return new ImportedWorkbook(workbookName, contracts, invoices);
    }

    private static Dictionary<string, List<Dictionary<string, string>>> ReadSheets(ZipArchive archive, IReadOnlyList<string> sharedStrings)
    {
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var relationships = LoadXml(archive, "xl/_rels/workbook.xml.rels")
            .Descendants(PackageRelationshipNamespace + "Relationship")
            .ToDictionary(
                relationship => (string?)relationship.Attribute("Id") ?? string.Empty,
                relationship => (string?)relationship.Attribute("Target") ?? string.Empty,
                StringComparer.Ordinal);

        var result = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Descendants(SpreadsheetNamespace + "sheet"))
        {
            var name = (string?)sheet.Attribute("name");
            var relationshipId = (string?)sheet.Attribute(RelationshipNamespace + "id");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relationshipId) || !relationships.TryGetValue(relationshipId, out var target))
            {
                continue;
            }

            var path = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : $"xl/{target}";
            result[name] = ReadTable(LoadXml(archive, path), sharedStrings);
        }

        return result;
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

    private static List<Dictionary<string, string>> ReadTable(XDocument sheet, IReadOnlyList<string> sharedStrings)
    {
        var rows = sheet.Descendants(SpreadsheetNamespace + "row")
            .Select(row => ReadRow(row, sharedStrings))
            .Where(row => row.Count != 0)
            .ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var headers = rows[0];
        return rows.Skip(1)
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
            .Select(row => ToFieldMap(headers, row))
            .Where(row => row.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            .ToList();
    }

    private static List<string> ReadRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new List<string>();
        var sequentialColumn = 0;
        foreach (var cell in row.Elements(SpreadsheetNamespace + "c"))
        {
            var reference = (string?)cell.Attribute("r");
            var column = reference is null ? sequentialColumn : ColumnIndex(reference);
            while (values.Count <= column)
            {
                values.Add(string.Empty);
            }

            values[column] = ReadCell(cell, sharedStrings);
            sequentialColumn = column + 1;
        }

        return values;
    }

    private static string ReadCell(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            return string.Concat(cell.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value));
        }

        var value = cell.Element(SpreadsheetNamespace + "v")?.Value ?? string.Empty;
        if (string.Equals(type, "s", StringComparison.Ordinal) && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        return value;
    }

    private static Dictionary<string, string> ToFieldMap(IReadOnlyList<string> headers, IReadOnlyList<string> row)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headers.Count; index++)
        {
            var header = NormaliseHeader(headers[index]);
            if (header.Length != 0)
            {
                fields[header] = index < row.Count ? row[index].Trim() : string.Empty;
            }
        }

        return fields;
    }

    private static ImportedContract ToContract(FrameworkCode framework, IReadOnlyDictionary<string, string> row)
    {
        var isVas = framework == FrameworkCode.VerticalApplicationSolutions;
        return new ImportedContract(
            Required(row, "supplierreferencenumber", "contract"),
            Required(row, "customerorganisationname", "contract"),
            Optional(row, "customeruniquereferencenumberurn"),
            ReadDate(Optional(row, "contractstartdate")),
            ReadDate(Optional(row, "contractenddate")),
            Optional(row, "lotnumber"),
            isVas ? Optional(row, "productservicegrouplevel1") : Optional(row, "servicegroup"),
            isVas ? Optional(row, "productservicegrouplevel2") : null,
            isVas ? Optional(row, "productservicedescription") : null,
            isVas ? Optional(row, "orderchannel") : null,
            isVas ? null : Optional(row, "digitalmarketplaceserviceid"),
            RequiredDecimal(row, "totalcontractvalue", "contract"));
    }

    private static ImportedInvoice ToInvoice(FrameworkCode framework, IReadOnlyDictionary<string, string> row)
    {
        var isVas = framework == FrameworkCode.VerticalApplicationSolutions;
        var totalCostExVat = RequiredDecimal(row, "totalcostexvat", "invoice");
        return new ImportedInvoice(
            Required(row, "supplierreferencenumber", "invoice"),
            Required(row, "customerorganisationname", "invoice"),
            Optional(row, "customeruniquereferencenumberurn"),
            ReadDate(Optional(row, "customerinvoicecreditnotedate")),
            Required(row, "customerinvoicecreditnotenumber", "invoice"),
            Optional(row, "lotnumber"),
            isVas ? Optional(row, "productservicegrouplevel1") : Optional(row, "servicegroup"),
            isVas ? Optional(row, "productservicegrouplevel2") : null,
            isVas ? Optional(row, "productservicedescription") : null,
            isVas ? Optional(row, "orderchannel") : null,
            isVas ? null : Optional(row, "digitalmarketplaceserviceid"),
            Optional(row, "unitofmeasure"),
            ReadDecimal(Optional(row, "quantity")),
            ReadDecimal(Optional(row, "priceperunit")),
            totalCostExVat,
            Optional(row, "originalvendor"),
            Optional(row, "subcontractorname"));
    }

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"The workbook is missing {path}.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string Required(IReadOnlyDictionary<string, string> row, string name, string rowType) =>
        Optional(row, name) ?? throw new InvalidDataException($"A {rowType} row is missing {name}.");

    private static decimal RequiredDecimal(IReadOnlyDictionary<string, string> row, string name, string rowType) =>
        ReadDecimal(Optional(row, name)) ?? throw new InvalidDataException($"A {rowType} row has an invalid {name} value.");

    private static string? Optional(IReadOnlyDictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static decimal? ReadDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalised = value.Replace("\u00A0", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(normalised, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : decimal.TryParse(normalised, NumberStyles.Number, CultureInfo.GetCultureInfo("en-GB"), out result)
                ? result
                : null;
    }

    private static DateOnly? ReadDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        }

        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy" };
        return DateOnly.TryParseExact(value, formats, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static int ColumnIndex(string reference)
    {
        var column = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter))
        {
            column = (column * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return column - 1;
    }

    private static string NormaliseHeader(string header) =>
        new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
