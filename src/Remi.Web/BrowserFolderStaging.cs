using Microsoft.AspNetCore.Components.Forms;

namespace Remi.Web;

/// <summary>
/// Materialises a browser-selected folder in a temporary local directory so existing migration
/// services can inspect its original hierarchy without relying on a server-side desktop dialog.
/// </summary>
public sealed class BrowserFolderStaging
{
    private const long MaximumFileSizeBytes = 512L * 1024 * 1024;

    public async Task<string> StageAsync(
        IReadOnlyList<IBrowserFile> files,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Select a folder that contains at least one file.");
        }

        if (files.Count != relativePaths.Count)
        {
            throw new InvalidOperationException("The browser did not provide a path for every selected file.");
        }

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Remi", "folder-staging", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var pair in files.Zip(relativePaths, (file, relativePath) => (file, relativePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.file.Size > MaximumFileSizeBytes)
                {
                    throw new InvalidOperationException($"{pair.file.Name} is larger than the {MaximumFileSizeBytes / 1024 / 1024} MB folder-import limit.");
                }

                var destinationPath = Path.Combine(stagingDirectory, SafeRelativePath(pair.relativePath));
                var destinationDirectory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException("The staged file path has no parent directory.");
                Directory.CreateDirectory(destinationDirectory);

                await using var input = pair.file.OpenReadStream(MaximumFileSizeBytes, cancellationToken);
                await using var output = File.Create(destinationPath);
                await input.CopyToAsync(output, cancellationToken);
            }

            return stagingDirectory;
        }
        catch
        {
            Delete(stagingDirectory);
            throw;
        }
    }

    public Task DeleteAsync(string? stagingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(stagingDirectory))
        {
            Delete(stagingDirectory);
        }

        return Task.CompletedTask;
    }

    private static string SafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("A selected file has an invalid relative path.");
        }

        var parts = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidOperationException("A selected file has an unsafe relative path.");
        }

        return Path.Combine(parts);
    }

    private static void Delete(string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }
}
