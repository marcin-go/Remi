using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Remi.Application;

namespace Remi.Infrastructure;

/// <summary>
/// Maintains a portable customer/URN suggestion index. The GOV.UK guidance page is stable; the
/// linked ODS file is deliberately resolved afresh because its versioned asset URL changes.
/// </summary>
public sealed class GcaCustomerUrnDirectory(
    HttpClient httpClient,
    IEvidenceArchive evidenceArchive,
    string indexFile) : ICustomerUrnDirectory
{
    private const string StableSourcePageUrl = "https://www.gov.uk/guidance/current-crown-commercial-service-suppliers-what-you-need-to-know#customer-unique-reference-number-urn-list";
    private const long MaximumDownloadBytes = 20 * 1024 * 1024;
    private static readonly Regex OdsLinkPattern = new(
        """href\s*=\s*["'](?<url>[^"']*GCA_Customer_URN_List_[^"']*\.ods[^"']*)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly XNamespace OfficeNamespace = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace TableNamespace = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace TextNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private readonly string indexFile = Path.GetFullPath(indexFile);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private CustomerUrnDirectoryIndex? cachedIndex;

    public async Task<CustomerUrnDirectoryStatus?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        (await GetIndexAsync(cancellationToken))?.Status;

    public async Task<IReadOnlyList<CustomerUrnSuggestion>> SearchAsync(
        string query,
        int maximumResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maximumResults <= 0)
        {
            return [];
        }

        var index = await GetIndexAsync(cancellationToken);
        if (index is null)
        {
            return [];
        }

        var search = query.Trim();
        return index.Entries
            .Where(item => item.OrganisationName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Urn.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => StartsWith(item.OrganisationName, search) ? 0 : 1)
            .ThenBy(item => item.OrganisationName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Min(maximumResults, 20))
            .ToList();
    }

    public async Task<CustomerUrnDirectoryRefresh> RefreshAsync(
        Guid evidenceId,
        CancellationToken cancellationToken = default)
    {
        var sourcePage = new Uri(StableSourcePageUrl);
        var html = await httpClient.GetStringAsync(sourcePage, cancellationToken);
        var downloadUri = ResolveOdsUri(html, sourcePage);
        var bytes = await DownloadAsync(downloadUri, cancellationToken);
        var organisations = ReadOrganisations(bytes);
        if (organisations.Count == 0)
        {
            throw new InvalidDataException("The downloaded URN list did not contain any customer organisations.");
        }

        var downloadedAtUtc = DateTimeOffset.UtcNow;
        var fileName = Path.GetFileName(downloadUri.LocalPath);
        await using var content = new MemoryStream(bytes, writable: false);
        var relativePath = Path.Combine(
            "reference-data",
            "customer-urn-lists",
            downloadedAtUtc.ToString("yyyy-MM-dd"),
            fileName);
        var archivedFile = await evidenceArchive.ArchiveAsync(
            new EvidenceArchiveRequest(
                fileName,
                relativePath,
                "application/vnd.oasis.opendocument.spreadsheet",
                content),
            cancellationToken);
        var status = new CustomerUrnDirectoryStatus(
            evidenceId,
            StableSourcePageUrl,
            downloadUri.AbsoluteUri,
            fileName,
            archivedFile.Sha256,
            organisations.Count,
            downloadedAtUtc);

        await indexGate.WaitAsync(cancellationToken);
        try
        {
            cachedIndex = new CustomerUrnDirectoryIndex(status, organisations);
            await SaveIndexUnsafeAsync(cachedIndex, cancellationToken);
        }
        finally
        {
            indexGate.Release();
        }

        return new CustomerUrnDirectoryRefresh(status, archivedFile, relativePath);
    }

    private async Task<CustomerUrnDirectoryIndex?> GetIndexAsync(CancellationToken cancellationToken)
    {
        await indexGate.WaitAsync(cancellationToken);
        try
        {
            if (cachedIndex is not null)
            {
                return cachedIndex;
            }

            if (!File.Exists(indexFile))
            {
                return null;
            }

            await using var stream = File.OpenRead(indexFile);
            cachedIndex = await JsonSerializer.DeserializeAsync<CustomerUrnDirectoryIndex>(
                stream,
                jsonOptions,
                cancellationToken);
            return cachedIndex;
        }
        finally
        {
            indexGate.Release();
        }
    }

    private async Task SaveIndexUnsafeAsync(CustomerUrnDirectoryIndex index, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(indexFile)
            ?? throw new InvalidOperationException("The URN directory index has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{indexFile}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, index, jsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, indexFile, overwrite: true);
    }

    private static Uri ResolveOdsUri(string html, Uri sourcePage)
    {
        var match = OdsLinkPattern.Match(html);
        if (!match.Success)
        {
            throw new InvalidDataException("The GOV.UK URN guidance page did not contain a current customer URN ODS download.");
        }

        var decoded = WebUtility.HtmlDecode(match.Groups["url"].Value);
        if (!Uri.TryCreate(sourcePage, decoded, out var resolved) ||
            !string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resolved.Host, "assets.publishing.service.gov.uk", StringComparison.OrdinalIgnoreCase) ||
            !resolved.AbsolutePath.EndsWith(".ods", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The URN download link was not a trusted GOV.UK ODS asset.");
        }

        return resolved;
    }

    private async Task<byte[]> DownloadAsync(Uri downloadUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
        {
            throw new InvalidDataException("The URN list is larger than Remi's 20 MB download safety limit.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await responseStream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            if (buffer.Length > MaximumDownloadBytes)
            {
                throw new InvalidDataException("The URN list is larger than Remi's 20 MB download safety limit.");
            }
        }

        return buffer.ToArray();
    }

    private static IReadOnlyList<CustomerUrnSuggestion> ReadOrganisations(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var contentEntry = archive.GetEntry("content.xml")
            ?? throw new InvalidDataException("The URN ODS file does not contain content.xml.");
        using var contentStream = contentEntry.Open();
        var document = XDocument.Load(contentStream);
        var urnTable = document
            .Descendants(TableNamespace + "table")
            .SingleOrDefault(table =>
                string.Equals((string?)table.Attribute(TableNamespace + "name"), "URN_List", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The URN ODS file does not contain its URN List tab.");
        var rows = urnTable
            .Elements(TableNamespace + "table-row")
            .Select(ReadRow)
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
            .ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var headers = rows[0].Select(NormaliseHeader).ToList();
        var urnColumn = headers.FindIndex(header => header == "urn");
        var organisationColumn = headers.FindIndex(header => header == "organisationname");
        if (urnColumn < 0 || organisationColumn < 0)
        {
            throw new InvalidDataException("The URN ODS file does not have URN and Organisation Name columns.");
        }

        return rows
            .Skip(1)
            .Where(row => row.Count > Math.Max(urnColumn, organisationColumn))
            .Select(row => new CustomerUrnSuggestion(
                row[urnColumn].Trim(),
                row[organisationColumn].Trim()))
            .Where(item => item.Urn.Length == 8 && item.Urn.All(char.IsDigit) && !string.IsNullOrWhiteSpace(item.OrganisationName))
            .DistinctBy(item => $"{item.Urn}\u001f{item.OrganisationName}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.OrganisationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ReadRow(XElement row)
    {
        var values = new List<string>();
        foreach (var cell in row.Elements())
        {
            if (cell.Name != TableNamespace + "table-cell" && cell.Name != TableNamespace + "covered-table-cell")
            {
                continue;
            }

            var repeated = int.TryParse((string?)cell.Attribute(TableNamespace + "number-columns-repeated"), out var count)
                ? Math.Clamp(count, 1, 1024)
                : 1;
            var value = (string?)cell.Attribute(OfficeNamespace + "value")
                ?? string.Join(" ", cell.Descendants(TextNamespace + "p").Select(paragraph => paragraph.Value));
            for (var index = 0; index < repeated; index++)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool StartsWith(string value, string query) =>
        value.StartsWith(query, StringComparison.OrdinalIgnoreCase);

    private static string NormaliseHeader(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record CustomerUrnDirectoryIndex(
        CustomerUrnDirectoryStatus Status,
        IReadOnlyList<CustomerUrnSuggestion> Entries);
}
