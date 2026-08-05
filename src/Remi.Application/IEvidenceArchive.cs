using Remi.Domain;

namespace Remi.Application;

/// <summary>
/// Stores original evidence files outside the SQLite register while keeping their metadata in it.
/// </summary>
public interface IEvidenceArchive
{
    Task<ArchivedEvidenceFile> ArchiveAsync(
        EvidenceArchiveRequest request,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(
        EvidenceRecord evidence,
        CancellationToken cancellationToken = default);
}

public sealed record EvidenceArchiveRequest(
    string FileName,
    string OriginalRelativePath,
    string ContentType,
    Stream Content);

public sealed record ArchivedEvidenceFile(
    string StoredRelativePath,
    long FileSizeBytes,
    string Sha256);

/// <summary>
/// Supports a one-time conversion from legacy source-tree evidence storage to Remi's flat,
/// content-addressed archive layout. The original source path remains in the evidence record.
/// </summary>
public interface IEvidenceArchiveLayoutMigrator
{
    /// <summary>
    /// Copies legacy evidence files to their flat destinations and verifies their integrity.
    /// It does not remove legacy copies or change the register.
    /// </summary>
    Task<IReadOnlyList<EvidenceArchiveRelocation>> PrepareFlatLayoutAsync(
        IReadOnlyList<EvidenceRecord> evidence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes legacy copies only after the register has been updated to the flat locations.
    /// </summary>
    Task<int> RemoveLegacyCopiesAsync(
        IReadOnlyList<EvidenceArchiveRelocation> relocations,
        CancellationToken cancellationToken = default);
}

public sealed record EvidenceArchiveRelocation(
    Guid EvidenceId,
    string PreviousStoredRelativePath,
    string FlatStoredRelativePath);
