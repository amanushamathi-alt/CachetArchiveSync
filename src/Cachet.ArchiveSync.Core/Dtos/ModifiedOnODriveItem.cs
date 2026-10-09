namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One item of <c>GET /sync/{taskId}/results</c>'s <c>modifiedOnODrive.items</c> array
/// (CAS-007 §6.2, FR-11). <see cref="FileId"/> is a <c>sync_result_id</c>, task-scoped (§1.7).
/// </summary>
public sealed record ModifiedOnODriveItem
{
    public required int FileId { get; init; }

    /// <summary>
    /// CAS-040 §6.5 — the file's <c>dbo.file_index.file_index_id</c>, used to open it in the File
    /// Viewer (<c>GET /files/{fileIndexId}</c>). Additive; <see cref="FileId"/>'s semantics are
    /// unchanged. Sourced from <c>odrive_file_index_id</c> — O-Drive is authoritative (CR-001 D5).
    /// </summary>
    public required int FileIndexId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    public required bool Sha1Changed { get; init; }

    /// <summary><c>Pending</c>, <c>Synced</c>, or <c>Failed</c> (CAS-007 §5).</summary>
    public required string SyncStatus { get; init; }
}
