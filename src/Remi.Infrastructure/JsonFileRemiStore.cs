using System.Text.Json;
using Remi.Application;
using Remi.Domain;

namespace Remi.Infrastructure;

public sealed class JsonFileRemiStore : IRemiStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly string databasePath;

    public JsonFileRemiStore(string? databasePath = null)
    {
        this.databasePath = Path.GetFullPath(databasePath ?? RemiDataPaths.DefaultDataFile);
    }

    public async Task<T> ReadAsync<T>(Func<RemiDatabase, T> reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return reader(await LoadUnsafeAsync(cancellationToken));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> UpdateAsync<T>(Func<RemiDatabase, T> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var database = await LoadUnsafeAsync(cancellationToken);
            var result = update(database);
            await SaveUnsafeAsync(database, cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemiDatabase> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return new RemiDatabase();
        }

        await using var stream = File.OpenRead(databasePath);
        return await JsonSerializer.DeserializeAsync<RemiDatabase>(stream, options, cancellationToken)
            ?? new RemiDatabase();
    }

    private async Task SaveUnsafeAsync(RemiDatabase database, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The Remi data path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{databasePath}.tmp";
        var previousPath = $"{databasePath}.previous";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, database, options, cancellationToken);
        }

        if (File.Exists(databasePath))
        {
            File.Copy(databasePath, previousPath, true);
        }

        File.Move(temporaryPath, databasePath, true);
    }
}
