using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Local-first query + self-healing soft-delete against <c>dbo.file_index</c> (CAS-006 §3, CAS-001
/// §6.3). See <see cref="FileIndexCandidateRow"/>'s XML doc — implemented against stored
/// procedures (<c>dbo.file_index_search</c>, <c>dbo.file_index_soft_delete_upd</c>) that have not
/// yet been authored by db-author (BLOCKED, flagged in the implementer's report).
/// </summary>
public interface IFileIndexRepository
{
    /// <summary>
    /// Local-first candidates, scoped to <paramref name="bucketName"/> + <paramref name="fileExtension"/>
    /// (§3). Only active (<c>is_active = 1</c>) rows are returned. These are pre-cross-check
    /// candidates, not confirmed matches (§6, §12).
    ///
    /// <c>Filename</c> rows: term already confirmed by a SQL <c>LIKE</c> filter (unchanged since
    /// CAS-006/CAS-011). <c>Content</c> rows (CAS-012 §2/§7): unfiltered candidates for content
    /// scanning — every active, in-scope-extension row for the bucket/extension is returned
    /// regardless of whether <paramref name="term"/> is actually present; the Worker resolves each
    /// one to a physical mirror file and performs the term match/snippet extraction itself
    /// (<see cref="IBucketMirrorRootRepository"/>, <see cref="IMirrorFileReader"/>,
    /// <see cref="IFileContentMatcher"/>) — this call performs no content matching.
    ///
    /// <paramref name="createdFrom"/> / <paramref name="createdTo"/> (CAS-038 §2/§7): both null =
    /// no date predicate (unchanged behavior); both set = both branches restricted to rows whose
    /// <c>created_at</c> falls within <c>[createdFrom 00:00, createdTo + 1 day 00:00)</c> UTC. Exactly
    /// one set is rejected by the stored procedure (the Api's V11 rejects it first).
    ///
    /// <paramref name="source"/> (CAS-047 §7): <c>ODrive</c> or <c>B2</c> restricts both branches to that
    /// source; null (default) = all sources, so legacy callers are unchanged.
    /// </summary>
    Task<IReadOnlyList<FileIndexCandidateRow>> SearchCandidatesAsync(
        string bucketName, string fileExtension, string term, DateOnly? createdFrom, DateOnly? createdTo, CancellationToken ct,
        string? source = null);

    /// <summary>
    /// Self-healing correction (§6, §7 V9, §12): sets <c>is_active = 0</c> for a local candidate
    /// the B2 cross-check no longer confirms exists (CAS-001 §6.3).
    /// </summary>
    Task SoftDeleteAsync(int fileIndexId, string updatedBy, CancellationToken ct);

    /// <summary>
    /// All <c>is_active = 1</c> rows for one <paramref name="source"/> (<c>ODrive</c> or
    /// <c>B2</c>) — CAS-007 §5 Comparison Algorithm step 1. See <see cref="FileIndexRow"/>'s XML
    /// doc — implemented against a stored procedure (<c>dbo.file_index_lst</c>) that has not been
    /// authored (BLOCKED, flagged in the implementer's report), fetched page-by-page internally
    /// (§5 step 5's "one paginated query per source, not one row at a time" performance note).
    /// </summary>
    Task<IReadOnlyList<FileIndexRow>> ListActiveBySourceAsync(string source, CancellationToken ct);

    /// <summary>
    /// Inserts a new <c>dbo.file_index</c> row (CAS-007 §6 <c>SyncCopy</c> — the new B2-side row
    /// for a freshly uploaded object). Calls <c>dbo.file_index_ins</c> (§5).
    /// </summary>
    Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        string source,
        string? bucketName,
        string relativePath,
        string fileName,
        string? fileExtension,
        long fileSizeBytes,
        DateTimeOffset modifiedAt,
        string? sha1Hash,
        DateTimeOffset? lastSeenAt,
        CancellationToken ct);

    /// <summary>
    /// Updates an existing <c>dbo.file_index</c> row in place (CAS-007 §6 <c>SyncUpdate</c>'s
    /// sha1/size/modified-at refresh, <c>SyncPurge</c>'s soft-delete). Calls
    /// <c>dbo.file_index_upd</c> (§5) — every parameter is optional/COALESCEd, only the fields the
    /// caller supplies are touched.
    /// </summary>
    Task UpdateAsync(
        int fileIndexId,
        string updatedBy,
        string? sha1Hash,
        long? fileSizeBytes,
        DateTimeOffset? modifiedAt,
        DateTimeOffset? lastSeenAt,
        bool? isActive,
        CancellationToken ct);

    /// <summary>
    /// CAS-040 §5/§6 step 1 — one row by <paramref name="fileIndexId"/> via
    /// <c>dbo.file_index_get</c>, inactive rows included. <c>null</c> when no row exists.
    /// </summary>
    Task<FileIndexRecord?> GetAsync(int fileIndexId, CancellationToken ct);
}
