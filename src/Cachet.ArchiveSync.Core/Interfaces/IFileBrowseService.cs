using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// CAS-043 §6 — folder listing built from <c>dbo.file_index</c> metadata for the Files screen.
/// Never touches the file system, B2 or file content. Browsing is not audited (§12).
/// </summary>
public interface IFileBrowseService
{
    Task<FileBrowseResult> BrowseAsync(
        string? folder, bool foldersOnly, int userId, CancellationToken ct);
}

/// <summary>CAS-043 §7 outcomes, mapped by <c>FilesController</c> to status codes/ProblemDetails.</summary>
public enum FileBrowseOutcome
{
    Success,

    /// <summary>V1, V2, V4 — 400 <c>validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary>V5 — 404 <c>folder-not-found</c>.</summary>
    FolderNotFound,

    /// <summary>V6 — 500 <c>bucket-not-configured</c>.</summary>
    BucketNotConfigured,

    /// <summary>V6 — 500 <c>mirror-mapping-missing</c>.</summary>
    MappingMissing,

    /// <summary>V9 — 500 <c>browse-failed</c>.</summary>
    BrowseFailed
}

public sealed record FileBrowseResult
{
    public required FileBrowseOutcome Outcome { get; init; }

    public FolderBrowseResponse? Response { get; init; }
}
