using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Remi.Application;

namespace Remi.Infrastructure;

/// <summary>
/// Transfers the portable datastore as a checked, versioned ZIP. Runtime logs and SQLite WAL
/// sidecars are deliberately excluded: the database is exported through SQLite's backup API.
/// </summary>
public sealed class RemiDataTransferService(string dataDirectory, string databasePath, SqliteRemiStore store) : IRemiDataTransfer
{
    private const string ManifestEntryName = "remi-data-transfer.json";
    private const int FormatVersion = 1;
    private static readonly TimeSpan PreparedExportLifetime = TimeSpan.FromHours(1);
    private readonly string dataDirectory = Path.GetFullPath(dataDirectory);
    private readonly string databasePath = Path.GetFullPath(databasePath);
    private readonly Dictionary<Guid, PreparedExport> preparedExports = [];
    private readonly Lock preparedExportsLock = new();

    public async Task<PreparedDataTransfer> PrepareExportAsync(CancellationToken cancellationToken = default)
    {
        RemoveExpiredPreparedExports();
        var id = Guid.NewGuid();
        var preparedAtUtc = DateTimeOffset.UtcNow;
        var fileName = $"remi-data-{preparedAtUtc:yyyyMMdd-HHmmss}.zip";
        var path = CreatePreparedExportPath(id);
        try
        {
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await ExportAsync(destination, cancellationToken);
            }

            var prepared = new PreparedDataTransfer(id, fileName, new FileInfo(path).Length, preparedAtUtc);
            lock (preparedExportsLock)
            {
                preparedExports.Add(id, new PreparedExport(prepared, path, preparedAtUtc.Add(PreparedExportLifetime)));
            }

            return prepared;
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            throw;
        }
    }

    public PreparedDataTransfer? GetPreparedExport(Guid id)
    {
        RemoveExpiredPreparedExports();
        lock (preparedExportsLock)
        {
            return preparedExports.TryGetValue(id, out var prepared) && File.Exists(prepared.Path)
                ? prepared.Summary
                : null;
        }
    }

    public Task<Stream?> OpenPreparedExportAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExpiredPreparedExports();
        lock (preparedExportsLock)
        {
            if (!preparedExports.TryGetValue(id, out var prepared) || !File.Exists(prepared.Path))
            {
                return Task.FromResult<Stream?>(null);
            }

            Stream stream = new FileStream(prepared.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<Stream?>(stream);
        }
    }

    public Task DiscardPreparedExportAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreparedExport? prepared;
        lock (preparedExportsLock)
        {
            prepared = preparedExports.Remove(id, out var value) ? value : null;
        }

        if (prepared is not null && File.Exists(prepared.Path))
        {
            File.Delete(prepared.Path);
        }

        return Task.CompletedTask;
    }

    public async Task ExportAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var stagingDirectory = CreateWorkingDirectory("export");
        try
        {
            var backupDatabasePath = Path.Combine(stagingDirectory, Path.GetFileName(databasePath));
            await store.BackupAsync(backupDatabasePath, cancellationToken);
            await CopyPersistentFilesAsync(dataDirectory, stagingDirectory, overwrite: false, skipDatabase: true, cancellationToken: cancellationToken);

            var files = await DescribeFilesAsync(stagingDirectory, cancellationToken);
            var manifest = new DataTransferManifest(FormatVersion, DateTimeOffset.UtcNow, files);
            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var source = new FileStream(Path.Combine(stagingDirectory, ToPlatformPath(file.Path)), FileMode.Open, FileAccess.Read, FileShare.Read);
                await source.CopyToAsync(entryStream, cancellationToken);
            }

            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, cancellationToken: cancellationToken);
        }
        finally
        {
            DeleteDirectory(stagingDirectory);
        }
    }

    public async Task ImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var stagingDirectory = CreateWorkingDirectory("import");
        var rollbackDirectory = CreateWorkingDirectory("rollback");
        try
        {
            await ExtractAndVerifyAsync(source, stagingDirectory, cancellationToken);
            await ValidateDatabaseAsync(Path.Combine(stagingDirectory, Path.GetFileName(databasePath)), cancellationToken);
            await store.BackupAsync(Path.Combine(rollbackDirectory, Path.GetFileName(databasePath)), cancellationToken);
            await CopyPersistentFilesAsync(dataDirectory, rollbackDirectory, overwrite: false, skipDatabase: true, cancellationToken: cancellationToken);

            await store.ReplaceDataAsync(async token =>
            {
                try
                {
                    ClearPersistentFiles(dataDirectory);
                    await CopyPersistentFilesAsync(stagingDirectory, dataDirectory, overwrite: true, cancellationToken: token);
                }
                catch
                {
                    ClearPersistentFiles(dataDirectory);
                    await CopyPersistentFilesAsync(rollbackDirectory, dataDirectory, overwrite: true, cancellationToken: token);
                    throw;
                }
            }, cancellationToken);
        }
        finally
        {
            DeleteDirectory(stagingDirectory);
            DeleteDirectory(rollbackDirectory);
        }
    }

    private async Task ExtractAndVerifyAsync(Stream source, string destinationDirectory, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntries = archive.Entries.Where(entry => string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal)).ToList();
        if (manifestEntries.Count != 1)
        {
            throw new InvalidDataException("The import file must contain exactly one Remi data-transfer manifest.");
        }

        DataTransferManifest? manifest;
        await using (var manifestStream = manifestEntries[0].Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<DataTransferManifest>(manifestStream, cancellationToken: cancellationToken);
        }

        if (manifest is null || manifest.FormatVersion != FormatVersion || manifest.Files is null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("This is not a supported Remi data-transfer package.");
        }

        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        if (expected.Count != manifest.Files.Count || !expected.ContainsKey(Path.GetFileName(databasePath)))
        {
            throw new InvalidDataException("The data-transfer manifest is invalid or does not contain a register database.");
        }

        var entries = archive.Entries.Where(entry => !string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal)).ToList();
        if (entries.Count != expected.Count || entries.Any(entry => !expected.ContainsKey(entry.FullName)))
        {
            throw new InvalidDataException("The package contents do not match its data-transfer manifest.");
        }

        foreach (var entry in entries)
        {
            if (!IsSafeRelativePath(entry.FullName) || !IsPersistentPath(ToPlatformPath(entry.FullName)) || entry.Length != expected[entry.FullName].Length)
            {
                throw new InvalidDataException("The package contains an invalid data file.");
            }

            var destination = Path.Combine(destinationDirectory, ToPlatformPath(entry.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
            }

            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected[entry.FullName].Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The checksum for '{entry.FullName}' does not match the package manifest.");
            }
        }
    }

    private static async Task ValidateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var quickCheck = connection.CreateCommand();
        quickCheck.CommandText = "PRAGMA quick_check;";
        if (!string.Equals((string?)await quickCheck.ExecuteScalarAsync(cancellationToken), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The transfer package's SQLite database failed its integrity check.");
        }

        await using var registerCheck = connection.CreateCommand();
        registerCheck.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'contracts';";
        if (await registerCheck.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidDataException("The transfer package does not contain a Remi register database.");
        }
    }

    private async Task CopyPersistentFilesAsync(string sourceDirectory, string destinationDirectory, bool overwrite, bool skipDatabase = false, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            if (!IsPersistentPath(relativePath) || (skipDatabase && string.Equals(Path.GetFullPath(sourcePath), databasePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<DataTransferFile>> DescribeFilesAsync(string directory, CancellationToken cancellationToken)
    {
        var files = new List<DataTransferFile>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(new DataTransferFile(relativePath, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))));
        }

        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
    }

    private void ClearPersistentFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => IsPersistentPath(Path.GetRelativePath(directory, path)))
                     .OrderByDescending(path => path.Length))
        {
            File.Delete(path);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath);
            }
        }
    }

    private static bool IsPersistentPath(string relativePath)
    {
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return !normalized.StartsWith($"logs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(normalized, "logs", StringComparison.OrdinalIgnoreCase)
               && !normalized.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
               && !normalized.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Contains('\\')
        && path.Split('/').All(part => !string.IsNullOrWhiteSpace(part) && part is not "." and not "..");

    private static string ToPlatformPath(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private string CreateWorkingDirectory(string purpose)
    {
        var parent = Path.GetDirectoryName(dataDirectory)
            ?? throw new InvalidOperationException("The Remi data directory has no parent directory.");
        var path = Path.Combine(parent, $".remi-data-transfer-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreatePreparedExportPath(Guid id)
    {
        var parent = Path.GetDirectoryName(dataDirectory)
            ?? throw new InvalidOperationException("The Remi data directory has no parent directory.");
        return Path.Combine(parent, $".remi-data-export-{id:N}.zip");
    }

    private void RemoveExpiredPreparedExports()
    {
        List<PreparedExport> expired;
        lock (preparedExportsLock)
        {
            var now = DateTimeOffset.UtcNow;
            expired = preparedExports.Values.Where(prepared => prepared.ExpiresAtUtc <= now).ToList();
            foreach (var item in expired)
            {
                preparedExports.Remove(item.Summary.Id);
            }
        }

        foreach (var item in expired)
        {
            if (File.Exists(item.Path))
            {
                File.Delete(item.Path);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record DataTransferManifest(int FormatVersion, DateTimeOffset ExportedAtUtc, IReadOnlyList<DataTransferFile> Files);
    private sealed record DataTransferFile(string Path, long Length, string Sha256);
    private sealed record PreparedExport(PreparedDataTransfer Summary, string Path, DateTimeOffset ExpiresAtUtc);
}
