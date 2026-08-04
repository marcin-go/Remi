using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

if (args.Length == 0 || args.Any(argument => string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("""
        Remi historical-report migration

        Usage:
          dotnet run --project src/Remi.Migration -- --source <source-data folder> [--data <remi-data.json>] [--validate]

        --validate reads and validates the workbooks without creating or updating Remi data.
        Without --validate, --data is required and must point to the data folder in the portable Remi installation.
        """);
    return 0;
}

var arguments = ReadArguments(args);
if (!arguments.TryGetValue("--source", out var sourceDirectory))
{
    Console.Error.WriteLine("Missing required --source argument. Use --help for usage.");
    return 2;
}

if (!Directory.Exists(sourceDirectory))
{
    Console.Error.WriteLine($"The source directory does not exist: {sourceDirectory}");
    return 2;
}

var validateOnly = arguments.ContainsKey("--validate");
var dataPath = arguments.GetValueOrDefault("--data");
if (!validateOnly && string.IsNullOrWhiteSpace(dataPath))
{
    Console.Error.WriteLine("Specify --data with the remi-data.json path in the portable Remi folder, or use --validate.");
    return 2;
}

IRemiStore store = validateOnly
    ? new InMemoryRemiStore()
    : new JsonFileRemiStore(dataPath);
IEvidenceArchive archive = validateOnly
    ? new ValidationEvidenceArchive()
    : new FileEvidenceArchive(RemiDataPaths.EvidenceDirectoryFor(dataPath!));
var workspace = new ReportingWorkspace(store, new XlsxMiWorkbookImporter(), archive, TimeProvider.System);

var sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
    .Where(path => !string.Equals(Path.GetFileName(path), "MI Reporting Ledger.xlsx", StringComparison.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToList();

var importedContracts = 0;
var importedInvoices = 0;
var archivedFiles = 0;
try
{
    foreach (var sourceFile in sourceFiles)
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
        var framework = FrameworkFor(sourceFile);
        var reportingMonth = ReportingMonthFor(sourceFile);
        await using var stream = File.OpenRead(sourceFile);

        if (string.Equals(Path.GetExtension(sourceFile), ".xlsx", StringComparison.OrdinalIgnoreCase) &&
            framework is not null &&
            reportingMonth is not null)
        {
            var result = await workspace.ImportWorkbookAsync(
                framework.Value,
                reportingMonth,
                Path.GetFileName(sourceFile),
                stream,
                relativePath);
            importedContracts += result.NewContracts;
            importedInvoices += result.NewInvoices;
            archivedFiles += result.EvidenceArchived ? 1 : 0;
            continue;
        }

        var wasArchived = await workspace.ArchiveEvidenceAsync(
            EvidenceKindFor(sourceFile),
            framework,
            reportingMonth,
            Path.GetFileName(sourceFile),
            relativePath,
            ContentTypeFor(sourceFile),
            ContractReferenceFor(sourceFile),
            stream);
        archivedFiles += wasArchived ? 1 : 0;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Migration stopped: {exception.Message}");
    return 1;
}

var dashboard = await workspace.GetDashboardAsync();
Console.WriteLine(validateOnly ? "Validation complete (no Remi data was written)." : "Migration complete.");
Console.WriteLine($"Reviewed {sourceFiles.Count} source files (excluding MI Reporting Ledger.xlsx).");
Console.WriteLine($"Imported {sourceFiles.Count(path => string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))} MI workbooks: {importedContracts} contracts and {importedInvoices} invoices.");
Console.WriteLine($"Archived {archivedFiles} original evidence file(s).");
Console.WriteLine($"Validation findings: {dashboard.Findings.Count}.");
foreach (var finding in dashboard.Findings)
{
    Console.WriteLine($"  {finding.Severity}: {finding.Code} — {finding.Message}");
}

return 0;

static Dictionary<string, string?> ReadArguments(IReadOnlyList<string> arguments)
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Count; index++)
    {
        var argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (string.Equals(argument, "--validate", StringComparison.OrdinalIgnoreCase))
        {
            values[argument] = null;
            continue;
        }

        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Argument {argument} needs a value.");
        }

        values[argument] = arguments[++index];
    }

    return values;
}

static FrameworkCode? FrameworkFor(string sourcePath) => sourcePath switch
{
    _ when sourcePath.Contains("RM1557.13", StringComparison.OrdinalIgnoreCase) => FrameworkCode.GCloud13,
    _ when sourcePath.Contains("RM1557.14", StringComparison.OrdinalIgnoreCase) => FrameworkCode.GCloud14,
    _ when sourcePath.Contains("RM6259", StringComparison.OrdinalIgnoreCase) => FrameworkCode.VerticalApplicationSolutions,
    _ => null,
};

static string? ReportingMonthFor(string sourcePath)
{
    var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
    while (directory is not null)
    {
        if (directory.Name is { Length: 6 } name && int.TryParse(name, out var value))
        {
            var year = value / 100;
            var month = value % 100;
            if (year is >= 2000 and <= 9999 && month is >= 1 and <= 12)
            {
                return $"{year:D4}-{month:D2}";
            }
        }

        directory = directory.Parent;
    }

    return null;
}

static EvidenceKind EvidenceKindFor(string sourcePath) =>
    ContractReferenceFor(sourcePath) is not null
        ? EvidenceKind.ContractDocument
        : EvidenceKind.SupportingDocument;

static string? ContractReferenceFor(string sourcePath)
{
    var match = Regex.Match(
        Path.GetFileNameWithoutExtension(sourcePath),
        @"\b[A-Z]{3}_\d{6}_[A-Z0-9]+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    return match.Success ? match.Value.ToUpperInvariant() : null;
}

static string ContentTypeFor(string sourcePath) => Path.GetExtension(sourcePath).ToLowerInvariant() switch
{
    ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    ".pdf" => "application/pdf",
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".txt" => "text/plain; charset=utf-8",
    _ => "application/octet-stream",
};

sealed class InMemoryRemiStore : IRemiStore
{
    private readonly RemiDatabase database = new();

    public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(reader(database));
    }

    public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(update(database));
    }
}

sealed class ValidationEvidenceArchive : IEvidenceArchive
{
    public async Task<ArchivedEvidenceFile> ArchiveAsync(
        EvidenceArchiveRequest request,
        CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await request.Content.ReadAsync(buffer, cancellationToken)) != 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
            size += bytesRead;
        }

        return new ArchivedEvidenceFile(
            Path.Combine("validation", request.FileName),
            size,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(null);
}
