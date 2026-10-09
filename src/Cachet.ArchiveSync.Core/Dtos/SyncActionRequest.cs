namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /sync/actions/copy</c> / <c>POST /sync/actions/update</c> request body (CAS-007 §6).
/// <see cref="FileIds"/> are <c>sync_result_id</c> values scoped to <see cref="TaskId"/> (the
/// originating <c>ContentScan</c> task), each expected to be a <c>Pending</c> row of the
/// endpoint-appropriate <c>comparison_type</c> (§7 V4/V5).
/// </summary>
public sealed record SyncActionRequest
{
    public required int TaskId { get; init; }

    public required IReadOnlyList<int> FileIds { get; init; }
}
