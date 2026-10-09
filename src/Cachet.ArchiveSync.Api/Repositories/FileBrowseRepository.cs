using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IFileBrowseRepository"/> (CAS-043 §5) over
/// <c>dbo.file_index_browse_lst</c>. The request's <see cref="CancellationToken"/> reaches the
/// command (SC-14). With <c>@folders_only = 1</c> the SP emits only the folders result set.
/// </summary>
public sealed class FileBrowseRepository : IFileBrowseRepository
{
    // 15 minutes: production-size B2 index is 876,554 rows (CAS-043 §5, SC-13).
    internal const int CommandTimeoutSeconds = 15 * 60;

    private readonly string _connectionString;

    public FileBrowseRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<FileBrowsePage> BrowseAsync(
        string source, string bucketName, string folderPath, bool foldersOnly, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@source", source);
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@folder_path", folderPath);
        parameters.Add("@folders_only", foldersOnly);
        parameters.Add("@totalFolders", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@totalFiles", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        List<FolderRow> folders;
        var files = new List<FileRow>();

        var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            "dbo.file_index_browse_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: CommandTimeoutSeconds,
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
        return new FileBrowsePage
        {
            Folders = folders
                .Select(row => new FileBrowseFolderRow(
                    row.folder_name,
                    SqlDateTimeMapping.ToUtcOffsetOrNull(row.latest_modified_at),
                    row.has_subfolders))
                .ToList(),
            Files = files
                .Select(row => new FileBrowseFileRow(
                    row.file_index_id, row.file_name, row.file_extension, row.file_size_bytes,
                    SqlDateTimeMapping.ToUtcOffset(row.modified_at)))
                .ToList(),
            TotalFolders = parameters.Get<int?>("@totalFolders") ?? 0,
            TotalFiles = parameters.Get<int?>("@totalFiles") ?? 0
        };
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
        public string file_name { get; set; } = string.Empty;
        public string? file_extension { get; set; }
        public long file_size_bytes { get; set; }
        public DateTime modified_at { get; set; }
    }
}
