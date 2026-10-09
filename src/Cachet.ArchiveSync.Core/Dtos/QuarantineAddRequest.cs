namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /search/results/quarantine</c> request body (CAS-006 §6.3, FR-09).
/// <see cref="FileIds"/> are <c>search_result_id</c> values scoped to <see cref="TaskId"/>
/// (§1.7) — the server resolves each to its <c>file_index_id</c> before writing
/// <c>dbo.quarantine_entry</c>.
/// </summary>
public sealed record QuarantineAddRequest
{
    public required int TaskId { get; init; }

    public required IReadOnlyList<int> FileIds { get; init; }
}
