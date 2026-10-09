using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Wraps <c>dbo.search_result_ins</c>/<c>_get</c>/<c>_lst</c> (CAS-006 §5).
/// </summary>
public interface ISearchResultRepository
{
    /// <summary>Inserts one confirmed match row (§6, §7 V9 — never called for a cross-check-excluded candidate).</summary>
    Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        int taskQueueId,
        int fileIndexId,
        string matchType,
        string? matchSnippet,
        string matchStatus,
        CancellationToken ct);

    /// <summary>
    /// CAS-047 §2/§7: set-based insert of every B2 <c>file_name</c> match for a Filename-mode task via
    /// <c>dbo.search_result_filename_ins</c> (status <c>Flagged</c>, no B2 call). Returns the inserted count.
    /// </summary>
    Task<int> InsertFilenameMatchesAsync(
        int taskQueueId, string bucketName, string? extension, string term, string actor, CancellationToken ct);

    /// <summary>Looks up one match row, joined to its <c>dbo.file_index</c> display fields. Returns null if not found.</summary>
    Task<SearchResultRow?> GetAsync(int searchResultId, CancellationToken ct);

    /// <summary>Paginated list for one task, optionally filtered by <paramref name="matchType"/> (§6.2).</summary>
    Task<(IReadOnlyList<SearchResultRow> Items, int TotalCount)> ListAsync(
        int taskQueueId, string? matchType, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Every <c>dbo.search_result</c> row across every <c>task_queue_id</c> (not scoped to one
    /// task), joined to <c>dbo.file_index</c> and filtered to active files (<c>is_active = 1</c>),
    /// via <c>dbo.search_result_lst_all</c> (CAS-010 §5). Backs the Pre-Action Impact Report's
    /// Filename/Content Matches panels (CAS-010 §3/§6.1/§7 V4). Not paginated — the first "all
    /// tasks" query against this table (every prior consumer scopes by <c>taskQueueId</c>).
    /// </summary>
    Task<IReadOnlyList<SearchResultRow>> ListAllActiveAsync(CancellationToken ct);
}
