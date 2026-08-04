using System.Security.Cryptography;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

/// <summary>
/// A portable, content-addressed archive. The original relative source path is retained as
/// a directory structure below the archive root, with a hash prefix allowing later versions
/// of the same source file to coexist.
/// </summary>
public sealed class FileEvidenceArchive(string archiveDirectory) : IEvidenceArchive, IResettableEvidenceArchive
{
    private readonly string archiveDirectory = Path.GetFullPath(archiveDirectory);

    public async Task<ArchivedEvidenceFile> ArchiveAsync(
        EvidenceArchiveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OriginalRelativePath);
        ArgumentNullException.ThrowIfNull(request.Content);

        Directory.CreateDirectory(archiveDirectory);
        var relativeSourcePath = NormaliseRelativePath(request.OriginalRelativePath, request.FileName);
        var temporaryDirectory = Path.Combine(archiveDirectory, ".incoming");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.tmp");

        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await request.Content.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hash.AppendData(buffer, 0, bytesRead);
                    size += bytesRead;
                }
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var sourceDirectory = Path.GetDirectoryName(relativeSourcePath) ?? string.Empty;
            var originalFileName = Path.GetFileName(relativeSourcePath);
            var storedRelativePath = Path.Combine(sourceDirectory, $"{sha256[..12]}-{originalFileName}");
            var targetPath = ResolveWithinArchive(storedRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (File.Exists(targetPath))
            {
                File.Delete(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }

            return new ArchivedEvidenceFile(storedRelativePath, size, sha256);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveWithinArchive(evidence.StoredRelativePath);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    /// <summary>
    /// Removes every archived file as part of an explicitly confirmed full repopulation.
    /// </summary>
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(archiveDirectory))
        {
            Directory.Delete(archiveDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string NormaliseRelativePath(string originalRelativePath, string fileName)
    {
        var parts = originalRelativePath.Replace('\\', '/').Trim()
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || Path.IsPathRooted(part)))
        {
            throw new ArgumentException("The original evidence path must be a safe relative path.", nameof(originalRelativePath));
        }

        var candidate = Path.Combine(parts);
        return string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : Path.Combine(Path.GetDirectoryName(candidate) ?? string.Empty, fileName);
    }

    private string ResolveWithinArchive(string storedRelativePath)
    {
        var rootWithSeparator = archiveDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? archiveDirectory
            : $"{archiveDirectory}{Path.DirectorySeparatorChar}";
        var path = Path.GetFullPath(Path.Combine(archiveDirectory, storedRelativePath));
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Evidence path resolves outside the Remi archive.");
        }

        return path;
    }
}
