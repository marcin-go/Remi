using Remi.Application;
using Remi.Infrastructure;

if (args.Length == 0 || args.Any(argument => string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("""
        Remi historical-report migration

        Usage:
          dotnet run --project src/Remi.Migration -- --source <source-data folder> [--data <remi-data.db>] [--validate | --repopulate]

        --validate reads and validates the workbooks without creating or updating Remi data.
        Without --validate, --data is required and must point to the SQLite database in the portable Remi installation.
        --repopulate validates the source, then replaces the complete local register and evidence archive.
        """);
    return 0;
}

var arguments = ReadArguments(args);
if (!arguments.TryGetValue("--source", out var sourceDirectory) || string.IsNullOrWhiteSpace(sourceDirectory))
{
    Console.Error.WriteLine("Missing required --source argument. Use --help for usage.");
    return 2;
}

var validateOnly = arguments.ContainsKey("--validate");
var repopulate = arguments.ContainsKey("--repopulate");
if (validateOnly && repopulate)
{
    Console.Error.WriteLine("Use either --validate or --repopulate, not both.");
    return 2;
}

var dataPath = arguments.GetValueOrDefault("--data");
if (!validateOnly && string.IsNullOrWhiteSpace(dataPath))
{
    Console.Error.WriteLine("Specify --data with the remi-data.db path in the portable Remi folder, or use --validate.");
    return 2;
}

var runner = new MigrationRunner(new XlsxMiWorkbookImporter(), new XlsxMiWorkbookExporter(), TimeProvider.System);
try
{
    MigrationReport report;
    if (validateOnly)
    {
        report = await runner.ValidateAsync(sourceDirectory);
    }
    else
    {
        var store = new SqliteRemiStore(dataPath);
        var archive = new FileEvidenceArchive(RemiDataPaths.EvidenceDirectoryFor(dataPath!));
        report = repopulate
            ? await runner.RepopulateAsync(sourceDirectory, store, archive)
            : await runner.ImportAsync(sourceDirectory, store, archive);
    }

    Console.WriteLine(report.ExistingDataReplaced
        ? "Repopulation complete. The previous local register and evidence archive were replaced."
        : report.DataWritten ? "Migration complete." : "Validation complete (no Remi data was written).");
    Console.WriteLine($"Reviewed {report.Plan.SourceFileCount} source files (excluding MI Reporting Ledger.xlsx).");
    Console.WriteLine($"Recognised {report.Plan.RecognisedMiWorkbookCount} MI workbook(s): {report.ImportedContracts} new contracts and {report.ImportedInvoices} new invoices.");
    Console.WriteLine($"Existing records skipped: {report.ExistingContracts} contracts and {report.ExistingInvoices} invoices.");
    Console.WriteLine($"Recovered {report.LedgerPaymentPositions} payment position(s) from the MI Reporting Ledger (the Ledger itself was not archived).");
    Console.WriteLine($"Archived {report.ArchivedEvidenceFiles} original evidence file(s).");
    Console.WriteLine($"Validation findings: {report.Findings.Count}.");
    foreach (var finding in report.Findings)
    {
        Console.WriteLine($"  {finding.Severity}: {finding.Code} — {finding.Message}");
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Migration stopped: {exception.Message}");
    return 1;
}

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

        if (string.Equals(argument, "--validate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "--repopulate", StringComparison.OrdinalIgnoreCase))
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
