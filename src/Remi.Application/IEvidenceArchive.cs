using Remi.Domain;

namespace Remi.Application;

/// <summary>
/// Stores original evidence files outside the JSON register while keeping their metadata in it.
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
