using System.Text.RegularExpressions;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

/// <summary>
/// Plans, validates and imports a historical source-data folder. Validation uses an in-memory
/// register and discard-only archive, so it never creates or changes the SQLite database.
/// </summary>
public sealed class MigrationRunner(
    IWorkbookImporter workbookImporter,
    IMiWorkbookExporter workbookExporter,
    TimeProvider timeProvider,
    ICustomerUrnDirectory? customerUrnDirectory = null)
{
    private readonly ICustomerUrnDirectory urnDirectory = customerUrnDirectory ?? UnavailableCustomerUrnDirectory.Instance;

    public Task<MigrationPlan> PlanAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BuildPlan(ReadSourceFiles(sourceDirectory, cancellationToken), sourceDirectory));
    }

    public async Task<MigrationReport> ValidateAsync(string sourceDirectory, CancellationToken cancellationToken = default) =>
        await ProcessAsync(
            sourceDirectory,
            new InMemoryRemiStore(),
            new ValidationEvidenceArchive(),
            dataWritten: false,
            existingDataReplaced: false,
            cancellationToken);

    public async Task<MigrationReport> ImportAsync(
        string sourceDirectory,
        IRemiStore store,
        IEvidenceArchive evidenceArchive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidenceArchive);
        return await ProcessAsync(sourceDirectory, store, evidenceArchive, dataWritten: true, existingDataReplaced: false, cancellationToken);
    }

    /// <summary>
    /// Rebuilds the local register and evidence archive from source data. The source folder is
    /// fully validated before any existing local data is discarded.
    /// </summary>
    public async Task<MigrationReport> RepopulateAsync(
        string sourceDirectory,
        IRemiStore store,
        IEvidenceArchive evidenceArchive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidenceArchive);

        if (store is not IRemiDataResetter dataResetter || evidenceArchive is not IResettableEvidenceArchive archiveResetter)
        {
            throw new InvalidOperationException("The configured Remi data store does not support full source-data repopulation.");
        }

        var preflight = await ValidateAsync(sourceDirectory, cancellationToken);
        if (preflight.Plan.SourceFileCount == 0)
        {
            throw new InvalidOperationException("The selected source folder is empty, so Remi will not discard the existing local register.");
        }

        await dataResetter.ResetAsync(cancellationToken);
        await archiveResetter.ResetAsync(cancellationToken);
        return await ProcessAsync(sourceDirectory, store, evidenceArchive, dataWritten: true, existingDataReplaced: true, cancellationToken);
    }

    private async Task<MigrationReport> ProcessAsync(
        string sourceDirectory,
        IRemiStore store,
        IEvidenceArchive archive,
        bool dataWritten,
        bool existingDataReplaced,
        CancellationToken cancellationToken)
    {
        var sourceFiles = ReadSourceFiles(sourceDirectory, cancellationToken);
        var plan = BuildPlan(sourceFiles, sourceDirectory);
        var workspace = new ReportingWorkspace(store, workbookImporter, workbookExporter, archive, urnDirectory, timeProvider);
        var importedContracts = 0;
        var existingContracts = 0;
        var importedInvoices = 0;
        var existingInvoices = 0;
        var archivedFiles = 0;
        var ledgerPaymentPositions = 0;
        var ledgerFindings = new List<ValidationFinding>();
        var suppliedReturnPeriods = sourceFiles
            .Where(file => file.IsMiWorkbook)
            .Select(file => new HistoricalReturnPeriod(file.Framework!.Value, file.ReportingMonth!))
            .Distinct()
            .ToList();

        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(sourceFile.FullPath);
            if (sourceFile.IsMiWorkbook)
            {
                var result = await workspace.ImportHistoricalWorkbookAsync(
                    sourceFile.Framework!.Value,
                    sourceFile.ReportingMonth!,
                    Path.GetFileName(sourceFile.FullPath),
                    stream,
                    sourceFile.RelativePath,
                    cancellationToken);
                importedContracts += result.NewContracts;
                existingContracts += result.ExistingContracts;
                importedInvoices += result.NewInvoices;
                existingInvoices += result.ExistingInvoices;
                archivedFiles += result.EvidenceArchived ? 1 : 0;
                continue;
            }

            var wasArchived = await workspace.ArchiveEvidenceAsync(
                EvidenceKindFor(sourceFile.FullPath),
                sourceFile.Framework,
                sourceFile.ReportingMonth,
                Path.GetFileName(sourceFile.FullPath),
                sourceFile.RelativePath,
                ContentTypeFor(sourceFile.FullPath),
                ContractReferenceFor(sourceFile.FullPath),
                stream,
                cancellationToken);
            archivedFiles += wasArchived ? 1 : 0;
        }

        var historicalFrameworks = suppliedReturnPeriods
            .Select(period => period.Framework)
            .Distinct()
            .ToList();
        var historicalMonths = suppliedReturnPeriods
            .Select(period => period.ReportingMonth)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var suppliedReturnPeriodSet = suppliedReturnPeriods.ToHashSet();
        var missingReturnPeriods = historicalFrameworks
            .SelectMany(framework => historicalMonths.Select(month => new HistoricalReturnPeriod(framework, month)))
            .Where(period => !suppliedReturnPeriodSet.Contains(period))
            .ToList();
        var inferredNilReturns = await workspace.EnsureHistoricalNilReturnsAsync(missingReturnPeriods, cancellationToken);

        var ledgerPath = FindLedgerPath(sourceDirectory);
        if (ledgerPath is not null)
        {
            var ledger = new LedgerWorkbookReader().Read(ledgerPath);
            ledgerFindings.AddRange(ledger.Findings);
            var ledgerImport = await workspace.ImportLedgerSchedulesAsync(ledger.Entries, cancellationToken: cancellationToken);
            importedContracts += ledgerImport.ContractsCreated;
            ledgerPaymentPositions = ledgerImport.PaymentPositionsAdded;
        }

        await workspace.CompleteMigratedRecordsAsync(cancellationToken: cancellationToken);

        var dashboard = await workspace.GetDashboardAsync(cancellationToken);
        return new MigrationReport(
            plan,
            dataWritten,
            existingDataReplaced,
            importedContracts,
            existingContracts,
            importedInvoices,
            existingInvoices,
            archivedFiles,
            dashboard.Findings.Concat(ledgerFindings).ToList(),
            ledgerPaymentPositions,
            suppliedReturnPeriods.Count,
            inferredNilReturns);
    }

    private static IReadOnlyList<SourceFile> ReadSourceFiles(string sourceDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"The source directory does not exist: {sourceDirectory}");
        }

        return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFileName(path), "MI Reporting Ledger.xlsx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var framework = FrameworkFor(path);
                var reportingMonth = ReportingMonthFor(path);
                return new SourceFile(
                    path,
                    Path.GetRelativePath(sourceDirectory, path),
                    framework,
                    reportingMonth,
                    string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase) && framework is not null && reportingMonth is not null);
            })
            .ToList();
    }

    private static string? FindLedgerPath(string sourceDirectory) =>
        Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), "MI Reporting Ledger.xlsx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static MigrationPlan BuildPlan(IReadOnlyList<SourceFile> sourceFiles, string sourceDirectory)
    {
        var recognisedWorkbooks = sourceFiles.Where(file => file.IsMiWorkbook).ToList();
        return new MigrationPlan(
            Path.GetFullPath(sourceDirectory),
            sourceFiles.Count,
            sourceFiles.Count(file => string.Equals(Path.GetExtension(file.FullPath), ".xlsx", StringComparison.OrdinalIgnoreCase)),
            recognisedWorkbooks.Count,
            sourceFiles.Count - recognisedWorkbooks.Count,
            recognisedWorkbooks
                .Select(file => new MigrationWorkbookPlan(file.RelativePath, file.Framework!.Value, file.ReportingMonth!))
                .ToList());
    }

    private static FrameworkCode? FrameworkFor(string sourcePath) => sourcePath switch
    {
        _ when sourcePath.Contains("RM1557.13", StringComparison.OrdinalIgnoreCase) => FrameworkCode.GCloud13,
        _ when sourcePath.Contains("RM1557.14", StringComparison.OrdinalIgnoreCase) => FrameworkCode.GCloud14,
        _ when sourcePath.Contains("RM6259", StringComparison.OrdinalIgnoreCase) => FrameworkCode.VerticalApplicationSolutions,
        _ => null,
    };

    private static string? ReportingMonthFor(string sourcePath)
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

    private static EvidenceKind EvidenceKindFor(string sourcePath) =>
        ContractReferenceFor(sourcePath) is not null
            ? EvidenceKind.ContractDocument
            : EvidenceKind.SupportingDocument;

    private static string? ContractReferenceFor(string sourcePath)
    {
        var match = Regex.Match(
            Path.GetFileNameWithoutExtension(sourcePath),
            @"\b[A-Z]{3}_\d{6}_[A-Z0-9]+\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string ContentTypeFor(string sourcePath) => Path.GetExtension(sourcePath).ToLowerInvariant() switch
    {
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private sealed record SourceFile(
        string FullPath,
        string RelativePath,
        FrameworkCode? Framework,
        string? ReportingMonth,
        bool IsMiWorkbook);

    private sealed class InMemoryRemiStore : IRemiStore
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

    private sealed class ValidationEvidenceArchive : IEvidenceArchive
    {
        public Task<ArchivedEvidenceFile> ArchiveAsync(EvidenceArchiveRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ArchivedEvidenceFile($"validation/{request.FileName}", 0, "validation-only"));
        }

        public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream?>(null);
        }

        public Task DeleteAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class UnavailableCustomerUrnDirectory : ICustomerUrnDirectory
    {
        public static readonly UnavailableCustomerUrnDirectory Instance = new();

        public Task<CustomerUrnDirectoryStatus?> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CustomerUrnDirectoryStatus?>(null);

        public Task<IReadOnlyList<CustomerUrnSuggestion>> SearchAsync(
            string query,
            int maximumResults = 8,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CustomerUrnSuggestion>>([]);

        public Task<CustomerUrnDirectoryRefresh> RefreshAsync(
            Guid evidenceId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CustomerUrnDirectoryRefresh>(new InvalidOperationException(
                "Customer URN data is unavailable while a migration is running."));
    }
}
