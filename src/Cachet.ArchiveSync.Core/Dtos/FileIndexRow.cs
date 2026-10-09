namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One <c>dbo.file_index</c> row (CAS-003 §5), read side. Used by the <c>ContentScan</c> Worker
/// task processor (CAS-007 §5 Comparison Algorithm) to build the O-Drive/B2 in-memory maps keyed
/// by <c>(BucketName, RelativePath)</c>.
///
/// BLOCKED (implementer, CAS-007): backed by <see cref="Interfaces.IFileIndexRepository.ListActiveBySourceAsync"/>,
/// implemented against a stored procedure named <c>dbo.file_index_lst</c> (following this
/// project's <c>schema.entity_action</c> convention) that has not been authored — CAS-007 §5's
/// stored-procedure table lists only <c>file_index_ins</c>/<c>_upd</c> as newly authored for this
/// spec, no full-listing procedure. Flagged for db-author, same treatment as CAS-006's
/// <c>file_index_search</c>/<c>task_queue_get</c> gaps (see <see cref="TaskQueueRow"/>'s XML doc).
/// </summary>
public sealed record FileIndexRow
{
    public required int FileIndexId { get; init; }

    /// <summary><c>ODrive</c> or <c>B2</c> (CAS-003 §5).</summary>
    public required string Source { get; init; }

    public string? BucketName { get; init; }

    public required string RelativePath { get; init; }

    public required string FileName { get; init; }

    public string? FileExtension { get; init; }

    public required long FileSizeBytes { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    /// <summary>Populated by the (out-of-scope, §4) mirror-population process — not computed
    /// fresh by the <c>ContentScan</c> comparison (§5 step 4).</summary>
    public string? Sha1Hash { get; init; }
}
