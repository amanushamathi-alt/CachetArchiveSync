using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Wraps <c>dbo.sync_result_ins</c>/<c>_get</c>/<c>_lst</c>/<c>_upd</c> (CAS-007 §5).
/// </summary>
public interface ISyncResultRepository
{
    /// <summary>One row per disagreement found by the <c>ContentScan</c> comparison (§5 Comparison Algorithm).</summary>
    Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        int taskQueueId,
        string comparisonType,
        int? odriveFileIndexId,
        int? b2FileIndexId,
        bool? sha1Changed,
        CancellationToken ct);

    /// <summary>Looks up one comparison row, joined to its <c>dbo.file_index</c> display fields. Returns null if not found.</summary>
    Task<SyncResultRow?> GetAsync(int syncResultId, CancellationToken ct);

    /// <summary>Paginated list for one <c>ContentScan</c> task, optionally filtered by <paramref name="comparisonType"/> (§6.2).</summary>
    Task<(IReadOnlyList<SyncResultRow> Items, int TotalCount)> ListAsync(
        int taskQueueId, string? comparisonType, int page, int pageSize, bool excludeSynced, CancellationToken ct);

    /// <summary><c>sync_status</c>/<c>sync_action_task_queue_id</c> transition only (§5, §6 Copy/Update/Purge sequences).</summary>
    Task UpdateStatusAsync(
        int syncResultId, string updatedBy, string syncStatus, int? syncActionTaskQueueId, CancellationToken ct);
}
