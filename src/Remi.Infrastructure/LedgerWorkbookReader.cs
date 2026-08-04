using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

/// <summary>
/// Reads the retired MI Reporting Ledger as migration input only. It deliberately does not archive
/// the workbook: the resulting records retain the source cell and the original payment notation in
/// their audit trail instead.
/// </summary>
public sealed partial class LedgerWorkbookReader
{
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    public LedgerWorkbookReadResult Read(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sharedStrings = ReadSharedStrings(archive);
        var workbook = LoadXml(archive, "xl/workbook.xml");
        var workbookRelationships = ReadRelationships(archive, "xl/_rels/workbook.xml.rels", "xl/workbook.xml");
        var entries = new List<LedgerContractScheduleEntry>();
        var findings = new List<ValidationFinding>();

        foreach (var sheet in workbook.Descendants(SpreadsheetNamespace + "sheet"))
        {
            var sheetName = (string?)sheet.Attribute("name");
            var relationshipId = (string?)sheet.Attribute(RelationshipNamespace + "id");
            if (sheetName is null || relationshipId is null || !TryGetFramework(sheetName, out var framework) || !workbookRelationships.TryGetValue(relationshipId, out var target))
            {
                continue;
            }

            var sheetPath = ResolvePartPath("xl/workbook.xml", target);
            var sheetDocument = LoadXml(archive, sheetPath);
            var comments = ReadComments(archive, sheetPath);
            foreach (var row in sheetDocument.Descendants(SpreadsheetNamespace + "row"))
            {
                var values = row.Elements(SpreadsheetNamespace + "c")
                    .ToDictionary(
                        cell => (string?)cell.Attribute("r") ?? string.Empty,
                        cell => ReadCell(cell, sharedStrings),
                        StringComparer.OrdinalIgnoreCase);
                var contractCell = values.FirstOrDefault(item => ColumnName(item.Key) == "B");
                if (string.IsNullOrWhiteSpace(contractCell.Key) || string.IsNullOrWhiteSpace(contractCell.Value) || !contractCell.Value.Contains("years", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var reportingMonth = ReportingMonth(values.FirstOrDefault(item => ColumnName(item.Key) == "A").Value);
                if (reportingMonth is null)
                {
                    findings.Add(Warning("LedgerReportingMonthUnreadable", $"{sheetName}!{contractCell.Key}: the Ledger row has no readable reporting month."));
                    continue;
                }

                comments.TryGetValue(contractCell.Key, out var comment);
                foreach (var fragment in ExtractContractFragments(contractCell.Value, sheetName, contractCell.Key, findings))
                {
                    var parsed = ContractPaymentScheduleNotation.Parse(fragment.Notation);
                    if (parsed.Schedule is null)
                    {
                        findings.Add(Warning("LedgerPaymentPlanUnreadable", $"{sheetName}!{contractCell.Key}, {fragment.SupplierReference}: {parsed.Error}"));
                        continue;
                    }

                    var fields = ExtractContractFields(comment, fragment.SupplierReference);
                    entries.Add(new LedgerContractScheduleEntry(
                        framework,
                        fragment.SupplierReference,
                        fields.CustomerName,
                        fields.CustomerUrn,
                        fields.StartDate,
                        fields.EndDate,
                        fields.LotNumber,
                        fields.ServiceGroup,
                        fields.DigitalMarketplaceServiceId,
                        fields.TotalContractValueExVat,
                        reportingMonth,
                        sheetName,
                        contractCell.Key,
                        parsed.Schedule));

                    if (parsed.Schedule.Positions.Any(position => position.HasUnresolvedUplift))
                    {
                        findings.Add(Warning("LedgerPaymentUpliftToConfirm", $"{sheetName}!{contractCell.Key}, {fragment.SupplierReference}: a payment position includes an uplift marker. Remi has retained its stated base value and marked it for confirmation."));
                    }
                }
            }
        }

        return new LedgerWorkbookReadResult(entries, findings);
    }

    private static IEnumerable<LedgerContractFragment> ExtractContractFragments(string value, string sheetName, string cellAddress, ICollection<ValidationFinding> findings)
    {
        foreach (Match match in SupplierReferencePattern().Matches(value))
        {
            var openingParenthesis = match.Index + match.Length - 1;
            if (openingParenthesis < 0)
            {
                findings.Add(Warning("LedgerContractNotationUnreadable", $"{sheetName}!{cellAddress}, {match.Groups["reference"].Value}: the Ledger contract notation has no payment-plan parentheses."));
                continue;
            }

            var closingParenthesis = MatchingParenthesis(value, openingParenthesis);
            if (closingParenthesis < 0)
            {
                findings.Add(Warning("LedgerContractNotationUnreadable", $"{sheetName}!{cellAddress}, {match.Groups["reference"].Value}: the Ledger contract notation has unmatched parentheses."));
                continue;
            }

            yield return new LedgerContractFragment(
                match.Groups["reference"].Value.ToUpperInvariant(),
                value[(openingParenthesis + 1)..closingParenthesis]);
        }
    }

    private static int MatchingParenthesis(string value, int openingParenthesis)
    {
        var depth = 0;
        for (var index = openingParenthesis; index < value.Length; index++)
        {
            if (value[index] == '(')
            {
                depth++;
            }
            else if (value[index] == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static LedgerContractFields ExtractContractFields(string? comment, string supplierReference)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return LedgerContractFields.Empty;
        }

        var referenceMatches = SupplierReferenceFieldPattern().Matches(comment);
        var referenceMatch = referenceMatches.Cast<Match>().FirstOrDefault(match => string.Equals(match.Groups["reference"].Value, supplierReference, StringComparison.OrdinalIgnoreCase));
        if (referenceMatch is null)
        {
            return LedgerContractFields.Empty;
        }

        var nextMatch = referenceMatches.Cast<Match>().FirstOrDefault(match => match.Index > referenceMatch.Index);
        var block = comment[referenceMatch.Index..(nextMatch?.Index ?? comment.Length)];
        return new LedgerContractFields(
            Field(block, "Customer Unique Reference Number (URN)"),
            Field(block, "Customer organisation name"),
            Date(Field(block, "Contract start date")),
            Date(Field(block, "Contract end date")),
            Field(block, "Lot number"),
            Field(block, "Service Group"),
            Field(block, "Digital Marketplace Service ID"),
            Decimal(Field(block, "Total contract value")));
    }

    private static string? Field(string block, string label)
    {
        var match = Regex.Match(block, $@"(?im)^\s*{Regex.Escape(label)}[ \t]+(?<value>.+?)\s*$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static decimal? Decimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalised = value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(normalised, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static string? ReportingMonth(string? cellValue)
    {
        if (!double.TryParse(cellValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            return null;
        }

        return DateTime.FromOADate(serial).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    private static bool TryGetFramework(string sheetName, out FrameworkCode framework)
    {
        framework = sheetName switch
        {
            "G-Cloud 13" => FrameworkCode.GCloud13,
            "G-Cloud 14" => FrameworkCode.GCloud14,
            "VAS" => FrameworkCode.VerticalApplicationSolutions,
            _ => default,
        };
        return sheetName is "G-Cloud 13" or "G-Cloud 14" or "VAS";
    }

    private static IReadOnlyDictionary<string, string> ReadComments(ZipArchive archive, string sheetPath)
    {
        var relationshipsPath = $"{Path.GetDirectoryName(sheetPath)!.Replace('\\', '/')}/_rels/{Path.GetFileName(sheetPath)}.rels";
        var relationshipEntry = archive.GetEntry(relationshipsPath);
        if (relationshipEntry is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var relationshipStream = relationshipEntry.Open();
        var relationships = XDocument.Load(relationshipStream);
        var commentTarget = relationships.Descendants(PackageRelationshipNamespace + "Relationship")
            .FirstOrDefault(relationship => ((string?)relationship.Attribute("Type"))?.EndsWith("/comments", StringComparison.OrdinalIgnoreCase) == true)
            ?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(commentTarget))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var comments = LoadXml(archive, ResolvePartPath(sheetPath, commentTarget));
        return comments.Descendants(SpreadsheetNamespace + "comment")
            .Where(comment => !string.IsNullOrWhiteSpace((string?)comment.Attribute("ref")))
            .ToDictionary(
                comment => (string)comment.Attribute("ref")!,
                comment => string.Concat(comment.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive archive, string relationshipPath, string sourcePart, bool missingIsEmpty = false)
    {
        var entry = archive.GetEntry(relationshipPath);
        if (entry is null && missingIsEmpty)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var relationships = LoadXml(archive, relationshipPath);
        return relationships.Descendants(PackageRelationshipNamespace + "Relationship")
            .ToDictionary(
                relationship => (string?)relationship.Attribute("Id") ?? string.Empty,
                relationship => (string?)relationship.Attribute("Target") ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string ResolvePartPath(string sourcePart, string target)
    {
        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            return target.TrimStart('/');
        }

        var source = new Uri($"https://remi.local/{sourcePart}", UriKind.Absolute);
        return new Uri(source, target).AbsolutePath.TrimStart('/');
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

    private static string ColumnName(string cellReference) => new(cellReference.TakeWhile(char.IsLetter).ToArray());

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"The workbook is missing {path}.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static ValidationFinding Warning(string code, string message) => new(FindingSeverity.Warning, code, message, "Ledger");

    [GeneratedRegex(@"(?<reference>\b[A-Z]{3}_\d{6}_[A-Z0-9]+\b)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupplierReferencePattern();

    [GeneratedRegex(@"Supplier reference number[ \t]+(?<reference>[A-Z]{3}_\d{6}_[A-Z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupplierReferenceFieldPattern();

    private sealed record LedgerContractFragment(string SupplierReference, string Notation);

    private sealed record LedgerContractFields(
        string? CustomerUrn,
        string? CustomerName,
        DateOnly? StartDate,
        DateOnly? EndDate,
        string? LotNumber,
        string? ServiceGroup,
        string? DigitalMarketplaceServiceId,
        decimal? TotalContractValueExVat)
    {
        public static readonly LedgerContractFields Empty = new(null, null, null, null, null, null, null, null);
    }
}

public sealed record LedgerWorkbookReadResult(
    IReadOnlyList<LedgerContractScheduleEntry> Entries,
    IReadOnlyList<ValidationFinding> Findings);
