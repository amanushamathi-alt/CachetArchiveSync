namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Entity mirror of one <c>dbo.quarantine_entry</c> row (CAS-006 §5) — schema owned by this
/// spec; only the add path (<see cref="Status"/> = <c>Active</c>) is written by this spec's code
/// (§0/§4).
/// </summary>
public sealed record QuarantineEntry
{
    public required int QuarantineEntryId { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required int FileIndexId { get; init; }

    public int? SearchResultId { get; init; }

    /// <summary><c>Flagged</c>/<c>Needs Review</c>/<c>Informational</c> — copied from
    /// <c>search_result.match_status</c> at insert time (CAS-006 §5).</summary>
    public required string MatchStatusSnapshot { get; init; }

    /// <summary><c>Active</c>/<c>Released</c>/<c>Deleted</c> — this spec only ever writes
    /// <c>Active</c> (§0).</summary>
    public required string Status { get; init; }
}
