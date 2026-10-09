using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IAuditLogRepository"/>, calling
/// <c>dbo.audit_log_ins</c> (CAS-005 §5) / <c>dbo.audit_log_lst</c> (CAS-009 §5).
/// </summary>
public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly string _connectionString;

    public AuditLogRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<int> InsertAsync(AuditLogEntry entry, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", entry.CreatedBy);
        parameters.Add("@updated_by", entry.UpdatedBy);
        parameters.Add("@user_id", entry.UserId);
        parameters.Add("@tab_module", entry.TabModule);
        parameters.Add("@action", entry.Action.ToString());
        parameters.Add("@file_path", entry.FilePath);
        parameters.Add("@task_queue_id", entry.TaskQueueId);
        parameters.Add("@result", entry.Result.ToString());
        parameters.Add("@result_detail", entry.ResultDetail);
        parameters.Add("@audit_log_id", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.audit_log_ins",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@audit_log_id");
    }

    public async Task<(IReadOnlyList<AuditLogRow> Items, int TotalCount)> ListAsync(
        string? tabModule, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, int page, int pageSize, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@tab_module", tabModule);
        parameters.Add("@date_from", dateFrom?.UtcDateTime);
        parameters.Add("@date_to", dateTo?.UtcDateTime);
        parameters.Add("@page", page);
        parameters.Add("@page_size", pageSize);
        parameters.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "dbo.audit_log_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var totalCount = parameters.Get<int>("@total_count");
        return (rows.Select(Map).ToList(), totalCount);
    }

    private static AuditLogRow Map(Row row) => new()
    {
        AuditLogId = row.audit_log_id,
        Username = row.username,
        TabModule = row.tab_module,
        Action = row.action,
        FilePath = row.file_path,
        CreatedAt = SqlDateTimeMapping.ToUtcOffset(row.created_at),
        Result = row.result,
        ResultDetail = row.result_detail
    };

    // Dapper's default mapping is case-insensitive against snake_case column names; a private
    // record keeps the SQL-shaped names out of the public AuditLogRow entity (Core), matching
    // SearchResultRepository's own Row convention.
    private sealed record Row(
        int audit_log_id,
        string username,
        string tab_module,
        string action,
        string? file_path,
        DateTime created_at,
        string result,
        string? result_detail);
}
