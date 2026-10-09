using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>CAS-043 §5 — calls <c>dbo.file_index_browse_lst</c>. Reads SQL Server only.</summary>
public interface IFileBrowseRepository
{
    /// <param name="folderPath">Full key path including the bucket prefix; <c>""</c> = bucket root.</param>
    Task<FileBrowsePage> BrowseAsync(
        string source, string bucketName, string folderPath, bool foldersOnly, CancellationToken ct);
}
