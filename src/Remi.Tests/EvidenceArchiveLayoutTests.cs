using System.Security.Cryptography;
using System.Text;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class EvidenceArchiveLayoutTests
{
    [Fact]
    public async Task New_evidence_is_stored_as_a_flat_content_addressed_file()
    {
        var root = TemporaryDirectory();
        try
        {
            var archive = new FileEvidenceArchive(root);
            var content = Encoding.UTF8.GetBytes("Approved G-Cloud evidence");
            await using var stream = new MemoryStream(content);

            var archived = await archive.ArchiveAsync(new EvidenceArchiveRequest(
                "return.xlsx",
                "RM1557.14 - G-Cloud/202607/return.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                stream));

            Assert.DoesNotContain(Path.DirectorySeparatorChar, archived.StoredRelativePath);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar, archived.StoredRelativePath);
            Assert.Equal($"{archived.Sha256[..12]}-return.xlsx", archived.StoredRelativePath);
            Assert.True(File.Exists(Path.Combine(root, archived.StoredRelativePath)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Legacy_source_tree_is_flattened_without_losing_provenance_or_integrity()
    {
        var root = TemporaryDirectory();
        try
        {
            var content = Encoding.UTF8.GetBytes("Historical return workbook");
            var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var originalRelativePath = "RM1557.14 - G-Cloud/202607/return.xlsx";
            var legacyStoredPath = $"RM1557.14 - G-Cloud/202607/{sha256[..12]}-return.xlsx";
            var legacyPath = Path.Combine(root, legacyStoredPath);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            await File.WriteAllBytesAsync(legacyPath, content);

            var evidence = new EvidenceRecord(
                Guid.NewGuid(),
                EvidenceKind.MonthlyMiWorkbook,
                FrameworkCode.GCloud14,
                "2026-07",
                "return.xlsx",
                originalRelativePath,
                legacyStoredPath,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                content.Length,
                sha256,
                null,
                DateTimeOffset.UtcNow);
            var database = new RemiDatabase { Evidence = [evidence] };
            var workspace = new ReportingWorkspace(
                new InMemoryStore(database),
                null!,
                null!,
                new FileEvidenceArchive(root),
                null!,
                TimeProvider.System);

            var result = await workspace.FlattenEvidenceArchiveAsync("test");
            var updated = Assert.Single(database.Evidence);
            var flatPath = $"{sha256[..12]}-return.xlsx";

            Assert.Equal(1, result.EvidenceRecordsUpdated);
            Assert.Equal(1, result.LegacyCopiesRemoved);
            Assert.Null(result.CleanupWarning);
            Assert.Equal(originalRelativePath, updated.OriginalRelativePath);
            Assert.Equal(flatPath, updated.StoredRelativePath);
            Assert.True(File.Exists(Path.Combine(root, flatPath)));
            Assert.False(File.Exists(legacyPath));
            Assert.Contains(database.AuditEvents, item => item.Action == "EvidenceArchiveFlattened");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class InMemoryStore(RemiDatabase database) : IRemiStore
    {
        public Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(reader(database));

        public Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default) =>
            Task.FromResult(update(database));
    }
}
