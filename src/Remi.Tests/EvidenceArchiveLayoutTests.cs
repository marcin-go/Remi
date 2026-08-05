using System.Text;
using Remi.Application;
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
}
