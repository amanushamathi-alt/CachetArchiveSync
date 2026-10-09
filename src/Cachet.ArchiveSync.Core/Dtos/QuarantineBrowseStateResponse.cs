namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /quarantine/browse/state</c> <c>200 OK</c> body (CAS-050 §8): quarantined direct-child file
/// ids plus per direct-child folder totals, for the Files explorer checkboxes.
/// </summary>
public sealed record QuarantineBrowseStateResponse
{
    public required IReadOnlyList<int> FileIndexIds { get; init; }

    public required IReadOnlyList<QuarantineBrowseStateFolder> Folders { get; init; }
}
