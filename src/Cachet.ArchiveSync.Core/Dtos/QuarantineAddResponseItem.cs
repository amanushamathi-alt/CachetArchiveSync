namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One item of <c>POST /search/results/quarantine</c>'s <c>200 OK</c> <c>items</c> array
/// (CAS-006 §6.3). <see cref="FileId"/> here is the file's stable identity
/// (<c>quarantine_entry_id</c>) — a different value than the <c>search_result_id</c> supplied in
/// the request (§1.7 semantics).
/// </summary>
public sealed record QuarantineAddResponseItem
{
    public required int FileId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required DateTimeOffset QuarantinedOn { get; init; }

    /// <summary>One of <c>Flagged</c>/<c>Needs Review</c>/<c>Informational</c> — copied from the
    /// originating <c>search_result.match_status</c> at insert time (CAS-006 §5).</summary>
    public required string Status { get; init; }
}
