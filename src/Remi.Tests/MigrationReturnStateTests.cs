using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class MigrationReturnStateTests
{
    [Fact]
    public async Task Historical_workbooks_are_submitted_and_missing_framework_months_are_nil()
    {
        var sourceDirectory = TemporaryDirectory();
        try
        {
            await AddWorkbookAsync(sourceDirectory, "RM1557.14 - G-Cloud 14", "202606", "gcloud-14-june.xlsx");
            await AddWorkbookAsync(sourceDirectory, "RM6259 - Vertical Application Solutions", "202607", "vas-july.xlsx");

            var database = new RemiDatabase();
            var runner = new MigrationRunner(new EmptyWorkbookImporter(), null!, new FixedTimeProvider());
            var report = await runner.ImportAsync(sourceDirectory, new InMemoryStore(database), new DiscardEvidenceArchive());

            Assert.Equal(2, report.SubmittedReturnReports);
            Assert.Equal(2, report.InferredNilReturns);
            Assert.Equal(ReturnStatus.Submitted, ReturnFor(database, FrameworkCode.GCloud14, "2026-06").Status);
            Assert.Equal(ReturnStatus.Submitted, ReturnFor(database, FrameworkCode.VerticalApplicationSolutions, "2026-07").Status);
            Assert.Equal(ReturnStatus.NilReturn, ReturnFor(database, FrameworkCode.GCloud14, "2026-07").Status);
            Assert.Equal(ReturnStatus.NilReturn, ReturnFor(database, FrameworkCode.VerticalApplicationSolutions, "2026-06").Status);
            Assert.DoesNotContain(database.MonthlyReturns, item => item.Framework == FrameworkCode.GCloud13);
            Assert.All(
                database.MonthlyReturns.Where(item => item.Status == ReturnStatus.Submitted),
                item => Assert.Null(item.SubmittedAtUtc));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    private static MonthlyReturn ReturnFor(RemiDatabase database, FrameworkCode framework, string reportingMonth) =>
        Assert.Single(database.MonthlyReturns.Where(item => item.Framework == framework && item.ReportMonth == reportingMonth));

    private static async Task AddWorkbookAsync(string sourceDirectory, string frameworkDirectory, string monthDirectory, string fileName)
    {
        var directory = Path.Combine(sourceDirectory, frameworkDirectory, monthDirectory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), "test workbook");
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class EmptyWorkbookImporter : IWorkbookImporter
    {
        public Task<ImportedWorkbook> ImportAsync(
            FrameworkCode framework,
            string workbookName,
            Stream workbook,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImportedWorkbook(workbookName, [], []));
    }

    private sealed class DiscardEvidenceArchive : IEvidenceArchive
    {
        public Task<ArchivedEvidenceFile> ArchiveAsync(EvidenceArchiveRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchivedEvidenceFile(request.FileName, 0, "test"));

        public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);
    }

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) =>
            Task.FromResult(update(database));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
    }
}
