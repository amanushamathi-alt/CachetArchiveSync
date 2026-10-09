using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ISearchResultRepository"/>, calling
/// <c>dbo.search_result_ins</c>/<c>_get</c>/<c>_lst</c> (CAS-006 §5).
/// </summary>
public sealed class SearchResultRepository : ISearchResultRepository
{
    private readonly string _connectionString;

    public SearchResultRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        int taskQueueId,
        int fileIndexId,
        string matchType,
        string? matchSnippet,
        string matchStatus,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@file_index_id", fileIndexId);
        parameters.Add("@match_type", matchType);
        parameters.Add("@match_snippet", matchSnippet);
        parameters.Add("@match_status", matchStatus);
        parameters.Add("@search_result_id", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.search_result_ins",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@search_result_id");
    }

    public async Task<int> InsertFilenameMatchesAsync(
        int taskQueueId, string bucketName, string? extension, string term, string actor, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@file_extension", extension);
        parameters.Add("@term", term);
        parameters.Add("@actor", actor);
        parameters.Add("@inserted", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.search_result_filename_ins",
            parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: 0,
            cancellationToken: ct));

        return parameters.Get<int>("@inserted");
    }

    public async Task<SearchResultRow?> GetAsync(int searchResultId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@search_result_id", searchResultId);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "dbo.search_result_get",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return row is null ? null : Map(row);
    }

    public async Task<(IReadOnlyList<SearchResultRow> Items, int TotalCount)> ListAsync(
        int taskQueueId, string? matchType, int page, int pageSize, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@match_type", matchType);
        parameters.Add("@page", page);
        parameters.Add("@pageSize", pageSize);
        parameters.Add("@totalCount", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "dbo.search_result_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var totalCount = parameters.Get<int>("@totalCount");
        return (rows.Select(Map).ToList(), totalCount);
    }

    /// <summary>CAS-010 §5 — backs the Pre-Action report's Filename/Content Matches panels, via
    /// <c>dbo.search_result_lst_all</c>. Not paginated (§3).</summary>
    public async Task<IReadOnlyList<SearchResultRow>> ListAllActiveAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "dbo.search_result_lst_all",
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return rows.Select(Map).ToList();
    }

    private static SearchResultRow Map(Row row) => new()
    {
        SearchResultId = row.search_result_id,
        TaskQueueId = row.task_queue_id,
        FileIndexId = row.file_index_id,
        MatchType = row.match_type,
        MatchSnippet = row.match_snippet,
        MatchStatus = row.match_status,
        CreatedAt = SqlDateTimeMapping.ToUtcOffset(row.created_at),
        FileName = row.file_name,
        RelativePath = row.relative_path,
        FileSizeBytes = row.file_size_bytes,
        ModifiedAt = SqlDateTimeMapping.ToUtcOffset(row.modified_at),
        BucketName = row.bucket_name,
        FileExtension = row.file_extension
    };

    // Dapper's default mapping is case-insensitive against snake_case column names; a private
    // record keeps the SQL-shaped names out of the public SearchResultRow entity (Core), matching
    // UserRepository's own UserRow convention.
    private sealed record Row(int search_result_id, int task_queue_id, int file_index_id, string match_type, string? match_snippet, string match_status, DateTime created_at, string file_name, string relative_path, long file_size_bytes, DateTime modified_at, string? bucket_name, string? file_extension);
}
