namespace Remi.Application;

/// <summary>
/// Resolves the current GCA customer URN list from its stable guidance page and maintains a
/// portable local index for registration suggestions.
/// </summary>
public interface ICustomerUrnDirectory
{
    Task<CustomerUrnDirectoryStatus?> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerUrnSuggestion>> SearchAsync(
        string query,
        int maximumResults = 8,
        CancellationToken cancellationToken = default);

    Task<CustomerUrnDirectoryRefresh> RefreshAsync(
        Guid evidenceId,
        CancellationToken cancellationToken = default);
}

public sealed record CustomerUrnSuggestion(
    string Urn,
    string OrganisationName);

public sealed record CustomerUrnDirectoryStatus(
    Guid EvidenceId,
    string SourcePageUrl,
    string ResolvedDownloadUrl,
    string FileName,
    string Sha256,
    int OrganisationCount,
    DateTimeOffset DownloadedAtUtc);

public sealed record CustomerUrnDirectoryRefresh(
    CustomerUrnDirectoryStatus Status,
    ArchivedEvidenceFile ArchivedFile,
    string OriginalRelativePath);
