namespace Remi.Application;

/// <summary>
/// Creates and restores portable, complete copies of Remi's local datastore.
/// An import replaces the current register and all locally retained evidence.
/// </summary>
public interface IRemiDataTransfer
{
    /// <summary>
    /// Builds a complete ZIP before it is offered for download so its size can be reported and
    /// the browser only receives a finished archive.
    /// </summary>
    Task<PreparedDataTransfer> PrepareExportAsync(CancellationToken cancellationToken = default);

    PreparedDataTransfer? GetPreparedExport(Guid id);

    Task<Stream?> OpenPreparedExportAsync(Guid id, CancellationToken cancellationToken = default);

    Task DiscardPreparedExportAsync(Guid id, CancellationToken cancellationToken = default);

    Task ExportAsync(Stream destination, CancellationToken cancellationToken = default);

    Task ImportAsync(Stream source, CancellationToken cancellationToken = default);
}

public sealed record PreparedDataTransfer(
    Guid Id,
    string FileName,
    long FileSizeBytes,
    DateTimeOffset PreparedAtUtc);
