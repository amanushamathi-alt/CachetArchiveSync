namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>One ancestor segment in a <see cref="FolderBrowseResponse"/> breadcrumb (CAS-043 §6).</summary>
public sealed record BreadcrumbItem
{
    public required string Name { get; init; }

    /// <summary>Root-relative path up to and including this segment.</summary>
    public required string Path { get; init; }
}
