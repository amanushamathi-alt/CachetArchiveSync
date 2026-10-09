namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>One child folder in a <see cref="FolderBrowseResponse"/> (CAS-043 §6).</summary>
public sealed record BrowseFolderItem
{
    public required string Name { get; init; }

    /// <summary>Root-relative path of the folder.</summary>
    public required string Path { get; init; }

    /// <summary>Newest <c>modified_at</c> among the folder's active descendants (UTC).</summary>
    public required DateTimeOffset? LatestModifiedAt { get; init; }

    /// <summary>
    /// True when the folder contains at least one non-empty-named subfolder among active rows
    /// (CAS-046). Defaults to true so a response without the field keeps the expand chevron.
    /// </summary>
    public bool HasSubfolders { get; init; } = true;
}
