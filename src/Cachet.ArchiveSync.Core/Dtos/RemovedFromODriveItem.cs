namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One item of <c>GET /sync/{taskId}/results</c>'s <c>removedFromODrive.items</c> array
/// (CAS-007 §6.2, FR-11). <see cref="FileId"/> is a <c>sync_result_id</c>, task-scoped (§1.7).
/// <see cref="LastSeen"/> is mapped from the B2-side <c>dbo.file_index.modified_at</c> —
/// <c>dbo.sync_result_get</c>/<c>_lst</c>'s join does not select <c>last_seen_at</c>, so
/// <c>modified_at</c> is the closest available "when was this last known to exist" field
/// (implementer's mapping choice, flagged for reviewer sign-off).
/// </summary>
public sealed record RemovedFromODriveItem
{
    public required int FileId { get; init; }

    /// <summary>
    /// CAS-040 §6.5 — the file's <c>dbo.file_index.file_index_id</c>, used to open it in the File
    /// Viewer (<c>GET /files/{fileIndexId}</c>). Additive; <see cref="FileId"/>'s semantics are
    /// unchanged. Sourced from <c>b2_file_index_id</c> (B2 mirror).
    /// </summary>
    public required int FileIndexId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required DateTimeOffset LastSeen { get; init; }

    /// <summary><c>Pending</c>, <c>Synced</c>, or <c>Failed</c> (CAS-007 §5).</summary>
    public required string SyncStatus { get; init; }
}
