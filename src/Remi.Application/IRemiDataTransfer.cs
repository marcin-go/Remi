namespace Remi.Application;

/// <summary>
/// Creates and restores portable, complete copies of Remi's local datastore.
/// An import replaces the current register and all locally retained evidence.
/// </summary>
public interface IRemiDataTransfer
{
    Task ExportAsync(Stream destination, CancellationToken cancellationToken = default);

    Task ImportAsync(Stream source, CancellationToken cancellationToken = default);
}
