using Microsoft.Data.Sqlite;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class DigitalMarketplaceServiceStoreTests
{
    [Fact]
    public async Task New_register_seeds_g_cloud_14_and_persists_framework_specific_local_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Remi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "remi.db");

        try
        {
            var workspace = Workspace(databasePath);

            var seededGCloud14 = await workspace.GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud14);
            var seededGCloud13 = await workspace.GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud13);
            var savedGCloud14 = await workspace.UpdateDigitalMarketplaceServicesAsync(
                FrameworkCode.GCloud14,
            [
                new DigitalMarketplaceService("115981361947474", "StatMap Cluster"),
            ]);
            var savedGCloud13 = await workspace.UpdateDigitalMarketplaceServicesAsync(
                FrameworkCode.GCloud13,
            [
                new DigitalMarketplaceService("115981361947474", "Historical StatMap Cluster", FrameworkCode.GCloud13),
            ]);
            var reopenedWorkspace = Workspace(databasePath);
            var reopenedGCloud14 = await reopenedWorkspace.GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud14);
            var reopenedGCloud13 = await reopenedWorkspace.GetDigitalMarketplaceServicesAsync(FrameworkCode.GCloud13);

            Assert.Equal(12, seededGCloud14.Count);
            Assert.Empty(seededGCloud13);
            Assert.Contains(seededGCloud14, service => service.ServiceId == "419925916803898" && service.Name == "HorizoNext Planning and Development Management (Development Control)");
            Assert.True(savedGCloud14.Succeeded);
            Assert.True(savedGCloud13.Succeeded);
            Assert.Equal([new DigitalMarketplaceService("115981361947474", "StatMap Cluster")], reopenedGCloud14);
            Assert.Equal([new DigitalMarketplaceService("115981361947474", "Historical StatMap Cluster", FrameworkCode.GCloud13)], reopenedGCloud13);
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
