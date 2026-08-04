namespace Remi.Application;

/// <summary>
/// Clears the local Remi register so it can be repopulated from the approved source data.
/// This is intentionally a reset, not a data-upgrade mechanism.
/// </summary>
public interface IRemiDataResetter
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Clears a local evidence archive as part of a complete source-data repopulation.
/// </summary>
public interface IResettableEvidenceArchive
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}
