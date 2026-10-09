namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /quarantine</c>'s <c>200 OK</c> response body (CAS-008 §6, FR-14). CAS-050 §8: paged —
/// <see cref="Items"/> is one page (25 or 50 rows) and <see cref="TotalCount"/> counts every Active row.
/// </summary>
public sealed record QuarantineListResponse
{
    public required IReadOnlyList<QuarantineListItem> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}
