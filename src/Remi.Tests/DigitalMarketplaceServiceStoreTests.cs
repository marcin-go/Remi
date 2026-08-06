using Microsoft.Data.Sqlite;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class DigitalMarketplaceServiceStoreTests
{
    [Fact]
    public async Task New_register_seeds_the_current_g_cloud_14_catalogue_and_preserves_local_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "remi.db");

        try
        {
            var workspace = Workspace(databasePath);

            var seeded = await workspace.GetDigitalMarketplaceServicesAsync();
            var saved = await workspace.UpdateDigitalMarketplaceServicesAsync(
            [
                new DigitalMarketplaceService("115981361947474", "StatMap Cluster"),
            ]);
            var reopened = await Workspace(databasePath).GetDigitalMarketplaceServicesAsync();

            Assert.Equal(12, seeded.Count);
            Assert.Contains(seeded, service => service.ServiceId == "419925916803898" && service.Name == "HorizoNext Planning and Development Management (Development Control)");
            Assert.True(saved.Succeeded);
            Assert.Equal([new DigitalMarketplaceService("115981361947474", "StatMap Cluster")], reopened);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ReportingWorkspace Workspace(string databasePath) =>
        new(new SqliteRemiStore(databasePath), null!, null!, null!, null!, TimeProvider.System);
}
