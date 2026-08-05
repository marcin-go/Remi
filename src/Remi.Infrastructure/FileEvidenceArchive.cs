using System.Security.Cryptography;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

/// <summary>
/// A portable, content-addressed archive. Every file is kept at the archive root with a
/// checksum prefix, while the original relative source path remains evidence metadata in
/// the register. This keeps the on-disk layout independent of the user's source folders.
/// </summary>
public sealed class FileEvidenceArchive(string archiveDirectory) : IEvidenceArchive, IEvidenceArchiveLayoutMigrator, IResettableEvidenceArchive
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
            var originalFileName = Path.GetFileName(relativeSourcePath);
            var storedRelativePath = FlatStoredRelativePath(sha256, originalFileName);
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

    public async Task<IReadOnlyList<EvidenceArchiveRelocation>> PrepareFlatLayoutAsync(
        IReadOnlyList<EvidenceRecord> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var relocations = evidence
            .Select(item => new EvidenceArchiveRelocation(
                item.Id,
                item.StoredRelativePath,
                FlatStoredRelativePath(item.Sha256, item.FileName)))
            .Where(item => !string.Equals(item.PreviousStoredRelativePath, item.FlatStoredRelativePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var relocation in relocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveWithinArchive(relocation.PreviousStoredRelativePath);
            var evidenceRecord = evidence.Single(item => item.Id == relocation.EvidenceId);
            await VerifyEvidenceFileAsync(source, evidenceRecord, cancellationToken);
        }

        foreach (var relocation in relocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveWithinArchive(relocation.PreviousStoredRelativePath);
            var target = ResolveWithinArchive(relocation.FlatStoredRelativePath);
            var evidenceRecord = evidence.Single(item => item.Id == relocation.EvidenceId);
            if (!File.Exists(target))
            {
                File.Copy(source, target);
            }

            await VerifyEvidenceFileAsync(target, evidenceRecord, cancellationToken);
        }

        return relocations;
    }

    public Task<int> RemoveLegacyCopiesAsync(
        IReadOnlyList<EvidenceArchiveRelocation> relocations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relocations);

        var removed = 0;
        foreach (var relocation in relocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var legacyPath = ResolveWithinArchive(relocation.PreviousStoredRelativePath);
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            File.Delete(legacyPath);
            RemoveEmptyParentDirectories(legacyPath);
            removed++;
        }

        return Task.FromResult(removed);
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

    private static string FlatStoredRelativePath(string sha256, string fileName)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length < 12 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Evidence must have a SHA-256 checksum before its archive path can be resolved.", nameof(sha256));
        }

        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Evidence must have a safe file name before its archive path can be resolved.", nameof(fileName));
        }

        return $"{sha256[..12].ToLowerInvariant()}-{name}";
    }

    private async Task VerifyEvidenceFileAsync(string path, EvidenceRecord evidence, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The archived evidence file was not found: {evidence.StoredRelativePath}", path);
        }

        var info = new FileInfo(path);
        if (info.Length != evidence.FileSizeBytes)
        {
            throw new InvalidDataException($"The archived evidence file size does not match the register for {evidence.FileName}.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(sha256, evidence.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The archived evidence checksum does not match the register for {evidence.FileName}.");
        }
    }

    private void RemoveEmptyParentDirectories(string filePath)
    {
        var root = archiveDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var directory = Directory.GetParent(filePath); directory is not null; directory = directory.Parent)
        {
            if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateFileSystemEntries(directory.FullName).Any())
            {
                return;
            }

            directory.Delete();
        }
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
