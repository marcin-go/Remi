namespace Remi.Infrastructure;

/// <summary>
/// Resolves the default data location for the portable edition.
/// </summary>
public static class RemiDataPaths
{
    public static string DefaultDataFile =>
        Path.Combine(AppContext.BaseDirectory, "data", "remi-data.json");

    public static string EvidenceDirectoryFor(string dataFile) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(dataFile))
                ?? throw new ArgumentException("The Remi data path has no parent directory.", nameof(dataFile)),
            "evidence");
}
