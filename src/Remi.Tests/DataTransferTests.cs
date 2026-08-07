using Microsoft.Data.Sqlite;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class DataTransferTests
{
    [Fact]
    public async Task Export_and_import_restore_the_register_and_all_persistent_data_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "data");
        var databasePath = Path.Combine(dataDirectory, "remi-data.db");
        Directory.CreateDirectory(Path.Combine(dataDirectory, "evidence"));

        try
        {
            var store = new SqliteRemiStore(databasePath);
            var transfer = new RemiDataTransferService(dataDirectory, databasePath, store);
            await SetServiceAsync(store, "source-service", "Source service");
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "evidence", "source.txt"), "source evidence");
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "reference-data.json"), "source reference data");

            var prepared = await transfer.PrepareExportAsync();
            Assert.True(prepared.FileSizeBytes > 0);
            Assert.EndsWith(".zip", prepared.FileName, StringComparison.OrdinalIgnoreCase);
            await using var package = new MemoryStream();
            await using (var preparedStream = Assert.IsAssignableFrom<Stream>(await transfer.OpenPreparedExportAsync(prepared.Id)))
            {
                await preparedStream.CopyToAsync(package);
            }
            await transfer.DiscardPreparedExportAsync(prepared.Id);
            Assert.Null(transfer.GetPreparedExport(prepared.Id));

            await SetServiceAsync(store, "target-service", "Target service");
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "evidence", "source.txt"), "changed evidence");
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "reference-data.json"), "changed reference data");

            package.Position = 0;
            await transfer.ImportAsync(package);

            var services = await store.ReadAsync(database => database.DigitalMarketplaceServices.ToList());
            Assert.Equal([new DigitalMarketplaceService("source-service", "Source service")], services);
            Assert.Equal("source evidence", await File.ReadAllTextAsync(Path.Combine(dataDirectory, "evidence", "source.txt")));
            Assert.Equal("source reference data", await File.ReadAllTextAsync(Path.Combine(dataDirectory, "reference-data.json")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task SetServiceAsync(SqliteRemiStore store, string serviceId, string name) =>
        store.UpdateAsync(database =>
        {
            database.DigitalMarketplaceServices.Clear();
            database.DigitalMarketplaceServices.Add(new DigitalMarketplaceService(serviceId, name));
            return 0;
        });
}
