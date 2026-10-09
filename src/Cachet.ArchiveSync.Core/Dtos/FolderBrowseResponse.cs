namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /files/browse</c> <c>200 OK</c> body (CAS-043 §6, proposed FR-29). Every path is relative
/// to the bucket's prefix root; no prefix, mirror root or B2 key appears (§12, SC-8, SC-12).
/// </summary>
public sealed record FolderBrowseResponse
{
    /// <summary>Last segment of the bucket prefix, or the bucket name when the prefix is empty.</summary>
    public required string RootName { get; init; }

    /// <summary>The listed folder, root-relative; <c>""</c> at the root.</summary>
    public required string Folder { get; init; }

    /// <summary>Immediate ancestor (<c>""</c> for a first-level folder); <c>null</c> at the root.</summary>
    public required string? Parent { get; init; }

    public required IReadOnlyList<BreadcrumbItem> Breadcrumb { get; init; }

    public required IReadOnlyList<BrowseFolderItem> Folders { get; init; }

    public required IReadOnlyList<BrowseFileItem> Files { get; init; }

    public required int TotalFolders { get; init; }

    public required int TotalFiles { get; init; }
}
