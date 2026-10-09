using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="IQuarantineBrowseService.AddAsync"/> (CAS-050 §8/§9).</summary>
public enum QuarantineBrowseAddOutcome
{
    /// <summary>Request shape invalid (§9 V1) — <c>422 validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary>File scope: not an active <c>file_index</c> row (§9 V3) — <c>404 file-not-found</c>.</summary>
    FileNotFound,

    /// <summary>Folder scope: the folder holds no active files (§9 V3) — <c>404 folder-not-found</c>.</summary>
    FolderNotFound,

    /// <summary>File scope only: the file is already Active-quarantined (§9 V4) — <c>409 already-quarantined</c>.</summary>
    AlreadyQuarantined,

    /// <summary>No <c>odrive_bucket_mapping</c> row for the bucket (CAS-053) — <c>500 mirror-mapping-missing</c>.</summary>
    MappingMissing,

    Completed
}

/// <summary>Result of one <see cref="IQuarantineBrowseService.AddAsync"/> call.</summary>
public sealed record QuarantineBrowseAddResult
{
    public required QuarantineBrowseAddOutcome Outcome { get; init; }

    public QuarantineBrowseAddResponse? Response { get; init; }
}

/// <summary>Outcome of <see cref="IQuarantineBrowseService.RemoveAsync"/> (CAS-050 §8/§9).</summary>
public enum QuarantineBrowseRemoveOutcome
{
    ValidationFailed,

    /// <summary>File scope: no Active row for the file — <c>404 not-quarantined</c>.</summary>
    NotQuarantined,

    /// <summary>Folder scope: the folder holds no active files — <c>404 folder-not-found</c>.</summary>
    FolderNotFound,

    /// <summary>No <c>odrive_bucket_mapping</c> row for the bucket (CAS-053) — <c>500 mirror-mapping-missing</c>.</summary>
    MappingMissing,

    Completed
}

/// <summary>Result of one <see cref="IQuarantineBrowseService.RemoveAsync"/> call.</summary>
public sealed record QuarantineBrowseRemoveResult
{
    public required QuarantineBrowseRemoveOutcome Outcome { get; init; }

    public QuarantineBrowseRemoveResponse? Response { get; init; }
}

/// <summary>Outcome of <see cref="IQuarantineBrowseService.GetStateAsync"/>.</summary>
public enum QuarantineBrowseStateOutcome
{
    ValidationFailed,

    /// <summary>No <c>odrive_bucket_mapping</c> row for the bucket (CAS-053) — <c>500 mirror-mapping-missing</c>.</summary>
    MappingMissing,

    Completed
}

/// <summary>Result of one <see cref="IQuarantineBrowseService.GetStateAsync"/> call.</summary>
public sealed record QuarantineBrowseStateResult
{
    public required QuarantineBrowseStateOutcome Outcome { get; init; }

    public QuarantineBrowseStateResponse? Response { get; init; }
}

/// <summary>
/// Files-browser Quarantine add/remove/state (CAS-050 §2, FR-09a). One row per file always — a folder
/// action expands server-side in one set-based statement. One audit row per add/remove action, written
/// before the success is returned (FR-23).
/// </summary>
public interface IQuarantineBrowseService
{
    Task<QuarantineBrowseAddResult> AddAsync(QuarantineBrowseRequest request, int actingUserId, CancellationToken ct);

    Task<QuarantineBrowseRemoveResult> RemoveAsync(QuarantineBrowseRequest request, int actingUserId, CancellationToken ct);

    Task<QuarantineBrowseStateResult> GetStateAsync(string? source, string? bucketName, string? folderPath, CancellationToken ct);
}
