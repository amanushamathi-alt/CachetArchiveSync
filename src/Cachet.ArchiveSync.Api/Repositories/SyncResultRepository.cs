using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ISyncResultRepository"/>, calling
/// <c>dbo.sync_result_ins</c>/<c>_get</c>/<c>_lst</c>/<c>_upd</c> (CAS-007 §5).
/// </summary>
public sealed class SyncResultRepository : ISyncResultRepository
{
    private readonly string _connectionString;

    public SyncResultRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        int taskQueueId,
        string comparisonType,
        int? odriveFileIndexId,
        int? b2FileIndexId,
        bool? sha1Changed,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@comparison_type", comparisonType);
        parameters.Add("@odrive_file_index_id", odriveFileIndexId);
        parameters.Add("@b2_file_index_id", b2FileIndexId);
        parameters.Add("@sha1_changed", sha1Changed);
        parameters.Add("@sync_result_id", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.sync_result_ins",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@sync_result_id");
    }

    public async Task<SyncResultRow?> GetAsync(int syncResultId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@sync_result_id", syncResultId);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "dbo.sync_result_get",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return row is null ? null : Map(row);
    }

    public async Task<(IReadOnlyList<SyncResultRow> Items, int TotalCount)> ListAsync(
        int taskQueueId, string? comparisonType, int page, int pageSize, bool excludeSynced, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@comparison_type", comparisonType);
        parameters.Add("@page", page);
        parameters.Add("@pageSize", pageSize);
        parameters.Add("@exclude_synced", excludeSynced);
        parameters.Add("@totalCount", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "dbo.sync_result_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var totalCount = parameters.Get<int>("@totalCount");
        return (rows.Select(Map).ToList(), totalCount);
    }

    public async Task UpdateStatusAsync(
        int syncResultId, string updatedBy, string syncStatus, int? syncActionTaskQueueId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@sync_result_id", syncResultId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@sync_status", syncStatus);
        parameters.Add("@sync_action_task_queue_id", syncActionTaskQueueId);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.sync_result_upd",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    private static SyncResultRow Map(Row row) => new()
    {
        SyncResultId = row.sync_result_id,
        TaskQueueId = row.task_queue_id,
        ComparisonType = row.comparison_type,
        OdriveFileIndexId = row.odrive_file_index_id,
        B2FileIndexId = row.b2_file_index_id,
        Sha1Changed = row.sha1_changed,
        SyncStatus = row.sync_status,
        SyncActionTaskQueueId = row.sync_action_task_queue_id,
        CreatedAt = SqlDateTimeMapping.ToUtcOffset(row.created_at),
        OdriveFileName = row.odrive_file_name,
        OdriveRelativePath = row.odrive_relative_path,
        OdriveBucketName = row.odrive_bucket_name,
        OdriveFileSizeBytes = row.odrive_file_size_bytes,
        OdriveModifiedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.odrive_modified_at),
        B2FileName = row.b2_file_name,
        B2RelativePath = row.b2_relative_path,
        B2FileSizeBytes = row.b2_file_size_bytes,
        B2ModifiedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.b2_modified_at)
    };

    // Dapper's default mapping is case-insensitive against snake_case column names; a private
    // record keeps the SQL-shaped names out of the public SyncResultRow entity (Core), matching
    // SearchResultRepository's own Row convention.
    private sealed record Row(
        int sync_result_id,
        int task_queue_id,
        string comparison_type,
        int? odrive_file_index_id,
        int? b2_file_index_id,
        bool? sha1_changed,
        string sync_status,
        int? sync_action_task_queue_id,
        DateTime created_at,
        // Positional: Dapper binds this constructor by column position AND name, so this order
        // must match dbo.sync_result_lst / dbo.sync_result_get's SELECT list exactly.
        string? odrive_file_name,
        string? odrive_relative_path,
        long? odrive_file_size_bytes,
        DateTime? odrive_modified_at,
        string? odrive_bucket_name,
        string? b2_file_name,
        string? b2_relative_path,
        long? b2_file_size_bytes,
        DateTime? b2_modified_at);
}
