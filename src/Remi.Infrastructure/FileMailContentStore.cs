using System.Security.Cryptography;
using Remi.Application;

namespace Remi.Infrastructure;

public sealed class FileMailContentStore(string rootDirectory) : IMailContentStore
{
    private readonly string root = Path.GetFullPath(rootDirectory);

    public async Task<(string StorageKey, long SizeBytes, string Sha256)> CreateAsync(
        Guid messageId,
        string eventType,
        DateTimeOffset createdAtUtc,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var safeEventType = string.Concat(eventType.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-'));
        var relativePath = Path.Combine(
            createdAtUtc.ToString("yyyy"),
            safeEventType,
            $"{messageId:N}-001.eml");
        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using (var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        return (relativePath.Replace(Path.DirectorySeparatorChar, '/'), content.Length, hash);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Resolve(storageKey.Replace('/', Path.DirectorySeparatorChar));
        Stream? stream = File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
        return Task.FromResult(stream);
    }

    private string Resolve(string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The mail content path leaves Remi's mail archive.");
        }

        return resolved;
    }
}
