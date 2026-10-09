using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>CAS-049 §7 — calls <c>dbo.search_result_browse_lst</c> and <c>dbo.search_result_ids_by_folder_lst</c>.</summary>
public interface ISearchResultBrowseRepository
{
    /// <param name="folderPath">Full key path including the bucket prefix; <c>""</c> = bucket root.</param>
    Task<SearchResultBrowsePage> BrowseAsync(
        int taskQueueId, string bucketName, string folderPath, bool foldersOnly, CancellationToken ct);

    /// <param name="folderPath">Full key path including the bucket prefix; <c>""</c> = whole task.</param>
    /// <param name="maxIds">Upper bound on rows returned (the caller passes cap + 1 to detect overflow).</param>
    /// <returns><c>search_result_id</c>s ascending, one per file, at every depth beneath the folder.</returns>
    Task<IReadOnlyList<int>> ListIdsByFolderAsync(
        int taskQueueId, string bucketName, string folderPath, int maxIds, CancellationToken ct);
}
