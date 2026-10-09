namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /quarantine/remove</c> request body (CAS-014 §8). <see cref="FileIds"/> are
/// <c>quarantine_entry_id</c> values, as returned by <c>GET /quarantine</c> — same identity
/// convention as <c>POST /quarantine/delete</c>'s <c>fileIds</c>, not the <c>search_result_id</c>
/// values used at add-time. No <c>confirm</c> flag — this action is not gated the way Permanent
/// Delete is (§8, §11).
/// </summary>
public sealed record QuarantineRemoveRequest
{
    public required IReadOnlyList<int> FileIds { get; init; }
}
