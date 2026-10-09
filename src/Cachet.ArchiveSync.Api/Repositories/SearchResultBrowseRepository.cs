using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ISearchResultBrowseRepository"/> (CAS-049 §7) over
/// <c>dbo.search_result_browse_lst</c> and <c>dbo.search_result_ids_by_folder_lst</c>. The request's
/// <see cref="CancellationToken"/> reaches the command.
/// </summary>
public sealed class SearchResultBrowseRepository : ISearchResultBrowseRepository
{
    private readonly string _connectionString;

    public SearchResultBrowseRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<SearchResultBrowsePage> BrowseAsync(
        int taskQueueId, string bucketName, string folderPath, bool foldersOnly, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@folder_path", folderPath);
        parameters.Add("@folders_only", foldersOnly);
        parameters.Add("@totalFolders", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@totalFiles", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        List<FolderRow> folders;
        var files = new List<FileRow>();

        var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            "dbo.search_result_browse_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: FileBrowseRepository.CommandTimeoutSeconds,
            cancellationToken: ct));
        await using (grid)
        {
            folders = (await grid.ReadAsync<FolderRow>()).ToList();
            if (!foldersOnly)
            {
                files = (await grid.ReadAsync<FileRow>()).ToList();
            }
        }

        // OUTPUT parameter values are populated only after every result set has been consumed.
        return new SearchResultBrowsePage
        {
            Folders = folders
                .Select(row => new FileBrowseFolderRow(
                    row.folder_name,
                    SqlDateTimeMapping.ToUtcOffsetOrNull(row.latest_modified_at),
                    row.has_subfolders))
                .ToList(),
            Files = files
                .Select(row => new SearchResultBrowseFileRow(
                    row.file_index_id, row.search_result_id, row.file_name, row.file_extension,
                    row.file_size_bytes, SqlDateTimeMapping.ToUtcOffset(row.modified_at), row.match_status))
                .ToList(),
            TotalFolders = parameters.Get<int?>("@totalFolders") ?? 0,
            TotalFiles = parameters.Get<int?>("@totalFiles") ?? 0
        };
    }

    public async Task<IReadOnlyList<int>> ListIdsByFolderAsync(
        int taskQueueId, string bucketName, string folderPath, int maxIds, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);

        var ids = await connection.QueryAsync<int>(new CommandDefinition(
            "dbo.search_result_ids_by_folder_lst",
            new
            {
                task_queue_id = taskQueueId,
                bucket_name = bucketName,
                folder_path = folderPath,
                max_ids = maxIds
            },
            commandType: CommandType.StoredProcedure,
            commandTimeout: FileBrowseRepository.CommandTimeoutSeconds,
            cancellationToken: ct));

        return ids.ToList();
    }

    private sealed class FolderRow
    {
        public string folder_name { get; set; } = string.Empty;
        public DateTime? latest_modified_at { get; set; }
        public bool has_subfolders { get; set; }
    }

    private sealed class FileRow
    {
        public int file_index_id { get; set; }
        public int search_result_id { get; set; }
        public string file_name { get; set; } = string.Empty;
        public string? file_extension { get; set; }
        public long file_size_bytes { get; set; }
        public DateTime modified_at { get; set; }
        public string? match_status { get; set; }
    }
}
