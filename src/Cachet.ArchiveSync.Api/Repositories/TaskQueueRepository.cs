using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="ITaskQueueRepository"/>, calling
/// <c>dbo.task_queue_ins</c>/<c>_claim</c>/<c>_upd</c> (CAS-006 §5) and, for
/// <see cref="GetAsync"/>, <c>dbo.task_queue_get</c> — see <see cref="TaskQueueRow"/>'s XML doc:
/// that last procedure has not yet been authored by db-author (BLOCKED, flagged in the
/// implementer's report). This method is written against its expected shape so the rest of the
/// call chain compiles and is unit-testable via a mocked repository; it will fail at runtime
/// against a real database until db-author adds the procedure.
/// </summary>
public sealed class TaskQueueRepository : ITaskQueueRepository
{
    private readonly string _connectionString;

    public TaskQueueRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        string taskType,
        int submittedByUserId,
        string? requestParameters,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@task_type", taskType);
        parameters.Add("@submitted_by_user_id", submittedByUserId);
        parameters.Add("@request_parameters", requestParameters);
        parameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@task_queue_id");
    }

    public async Task<ClaimedTaskRow?> ClaimNextAsync(string updatedBy, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@updated_by", updatedBy);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition(
            "dbo.task_queue_claim",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return row is null
            ? null
            : new ClaimedTaskRow
            {
                TaskQueueId = row.task_queue_id,
                TaskType = row.task_type,
                SubmittedByUserId = row.submitted_by_user_id,
                RequestParameters = row.request_parameters,
                SubmittedAt = SqlDateTimeMapping.ToUtcOffset(row.submitted_at),
                ResumeWatermarkFileIndexId = row.resume_watermark_file_index_id,
                RestartedFromTaskQueueId = row.restarted_from_task_queue_id
            };
    }

    public async Task UpdateStatusAsync(
        int taskQueueId, string updatedBy, string status, string? resultDetail, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@status", status);
        parameters.Add("@result_detail", resultDetail);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_upd",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    /// <summary>
    /// BLOCKED (implementer, CAS-006): calls <c>dbo.task_queue_get</c>, which does not exist yet
    /// — see this class's XML doc and <see cref="TaskQueueRow"/>'s.
    /// </summary>
    ///  /// <summary>
    /// CAS-026 §0 — calls <c>dbo.task_queue_get</c>, which already exists (added by the CAS-006
    /// addendum migration <c>20260922_173000__CAS-006-keyword-search-api-addendum.sql</c>). The
    /// prior "BLOCKED" note on this method was stale documentation, not an actual gap; corrected
    /// here.
    /// </summary>
    public async Task<TaskQueueRow?> GetAsync(int taskQueueId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<GetRow>(new CommandDefinition(
            "dbo.task_queue_get",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return row is null
            ? null
            : new TaskQueueRow
            {
                TaskQueueId = row.task_queue_id,
                TaskType = row.task_type,
                Status = row.status,
                SubmittedByUserId = row.submitted_by_user_id,
                SubmittedAt = SqlDateTimeMapping.ToUtcOffset(row.submitted_at),
                StartedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.started_at),
                CompletedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.completed_at),
                RequestParameters = row.request_parameters,
                ResultDetail = row.result_detail,
                ItemsProcessed = row.items_processed,
                ItemsTotal = row.items_total,
                ProgressUpdatedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.progress_updated_at),
                ResultAcknowledgedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.result_acknowledged_at)
            };
    }

    /// <summary>
    /// CAS-029 §3.4/§5 — calls <c>dbo.task_queue_progress_upd</c> and maps its single-row
    /// <c>SELECT rows_affected</c> result: <c>true</c> when the <c>Running</c> row was updated,
    /// <c>false</c> when it is no longer <c>Running</c> (or unknown).
    /// </summary>
    public async Task<bool> UpdateProgressAsync(
        int taskQueueId, string updatedBy, int itemsProcessed, int itemsTotal, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@items_processed", itemsProcessed);
        parameters.Add("@items_total", itemsTotal);

        await using var connection = new SqlConnection(_connectionString);

        var rowsAffected = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.task_queue_progress_upd",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return rowsAffected > 0;
    }

    /// <summary>
    /// CAS-016 §7 — calls <c>dbo.task_queue_restart_ins</c> and maps its <c>@result_code</c>
    /// OUTPUT value (<c>'RESTARTED'|'NOT_FOUND'|'NOT_FAILED'|'NOT_OWNER'</c>) to
    /// <see cref="RestartTaskOutcome"/>. Ownership and <c>Failed</c>-state checks happen inside
    /// the procedure's own transaction (§11), not here.
    /// </summary>
    public async Task<RestartTaskResult> RestartAsync(
        int taskQueueId, int requestingUserId, string createdBy, string updatedBy, CancellationToken ct,
        int? resumeMaxAgeHours = null)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@requesting_user_id", requestingUserId);
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@new_task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@result_code", dbType: DbType.String, size: 20, direction: ParameterDirection.Output);
        parameters.Add("@resume_max_age_hours", resumeMaxAgeHours);
        parameters.Add("@resumed_from_file_index_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@carried_over_count", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@resume_note", dbType: DbType.String, size: 40, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_restart_ins",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var resultCode = parameters.Get<string>("@result_code");

        return resultCode switch
        {
            "RESTARTED" => new RestartTaskResult
            {
                Outcome = RestartTaskOutcome.Restarted,
                NewTaskQueueId = parameters.Get<int?>("@new_task_queue_id"),
                ResumedFromFileIndexId = parameters.Get<int?>("@resumed_from_file_index_id"),
                CarriedOverCount = parameters.Get<int?>("@carried_over_count") ?? 0,
                ResumeNote = parameters.Get<string?>("@resume_note")
            },
            "NOT_FOUND" => new RestartTaskResult { Outcome = RestartTaskOutcome.NotFound },
            "NOT_FAILED" => new RestartTaskResult { Outcome = RestartTaskOutcome.NotRestartable },
            "NOT_OWNER" => new RestartTaskResult { Outcome = RestartTaskOutcome.NotOwner },
            _ => throw new InvalidOperationException(
                $"Unrecognized @result_code '{resultCode}' returned by dbo.task_queue_restart_ins.")
        };
    }

    /// <summary>
    /// CAS-054 §7 — calls <c>dbo.task_queue_resume_watermark_upd</c> and maps its single-row
    /// <c>SELECT rows_affected</c> result (0 = not Running / not newer — not an error).
    /// </summary>
    public async Task<bool> UpdateResumeWatermarkAsync(
        int taskQueueId, string updatedBy, int resumeWatermarkFileIndexId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@resume_watermark_file_index_id", resumeWatermarkFileIndexId);

        await using var connection = new SqlConnection(_connectionString);

        var rowsAffected = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.task_queue_resume_watermark_upd",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return rowsAffected > 0;
    }

    /// <summary>
    /// CAS-028 §3.4/§5 — calls <c>dbo.task_queue_sweep_interrupted</c> (atomic <c>Running</c> →
    /// <c>Failed</c> + one audit row per task, committed together inside the procedure) and maps
    /// its final <c>SELECT</c> of swept rows.
    /// </summary>
    public async Task<IReadOnlyList<SweptTaskRow>> SweepInterruptedAsync(
        string updatedBy, string failureReason, CancellationToken ct,
        int? resumeMaxAgeHours = null, int? autoResumeMaxAttempts = null)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@failure_reason", failureReason);
        parameters.Add("@resume_max_age_hours", resumeMaxAgeHours);
        parameters.Add("@auto_resume_max_attempts", autoResumeMaxAttempts);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<SweptRow>(new CommandDefinition(
            "dbo.task_queue_sweep_interrupted",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return rows
            .Select(row => new SweptTaskRow
            {
                TaskQueueId = row.task_queue_id,
                TaskType = row.task_type,
                SubmittedByUserId = row.submitted_by_user_id,
                ResumedAsTaskQueueId = row.resumed_as_task_queue_id,
                AutoResumeSuppressed = row.auto_resume_suppressed,
                CarriedOverCount = row.carried_over_count
            })
            .ToList();
    }

    /// <summary>
    /// CAS-030 §5 — calls <c>dbo.task_queue_lst</c> (<c>@total_count</c> OUTPUT, then the page),
    /// following <see cref="AuditLogRepository.ListAsync"/>'s paging convention.
    /// </summary>
    ///  /// <summary>
    /// CAS-026 §5/§6.1 — calls <c>dbo.task_queue_lst</c>. Mirrors
    /// <see cref="AuditLogRepository.ListAsync"/>'s exact <c>@total_count OUTPUT</c> pattern.
    /// </summary>
    public async Task<(IReadOnlyList<TaskQueueListRow> Items, int TotalCount)> ListAsync(
        int submittedByUserId, string? taskType, string? status, int page, int pageSize, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@submitted_by_user_id", submittedByUserId);
        parameters.Add("@task_type", taskType);
        parameters.Add("@status", status);
        parameters.Add("@page", page);
        parameters.Add("@page_size", pageSize);
        parameters.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<ListRow>(new CommandDefinition(
            "dbo.task_queue_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var items = rows
            .Select(row => new TaskQueueListRow
            {
                TaskQueueId = row.task_queue_id,
                TaskType = row.task_type,
                SubmittedByUserId = row.submitted_by_user_id,
                Username = row.username,
                SubmittedAt = SqlDateTimeMapping.ToUtcOffset(row.submitted_at),
                StartedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.started_at),
                CompletedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.completed_at),
                Status = row.status,
                ResultAcknowledgedAt = SqlDateTimeMapping.ToUtcOffsetOrNull(row.result_acknowledged_at)
            })
            .ToList();

        // Read after the reader is fully consumed (ToList above) — OUTPUT values are only
        // populated once the result set has been drained.
        var totalCount = parameters.Get<int>("@total_count");
        return (items, totalCount);
    }

    /// <summary>CAS-030 §5 — calls <c>dbo.task_queue_summary_get</c>; always returns one row.</summary>
    public async Task<TaskSummaryResponse> GetSummaryAsync(int submittedByUserId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@submitted_by_user_id", submittedByUserId);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleAsync<SummaryRow>(new CommandDefinition(
            "dbo.task_queue_summary_get",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return new TaskSummaryResponse
        {
            ActiveCount = row.active_count,
            UnacknowledgedTerminalCount = row.unacknowledged_terminal_count
        };
    }


    /// <summary>
    /// CAS-030 §5 — calls <c>dbo.task_queue_acknowledge_upd</c> and maps its <c>@outcome</c>
    /// OUTPUT (<c>0</c> acknowledged/already acknowledged, <c>1</c> not found or not owned,
    /// <c>2</c> not finished). Ownership is enforced inside the procedure, not here.
    /// </summary>
    public async Task<AcknowledgeTaskOutcome> AcknowledgeAsync(
        int taskQueueId, int submittedByUserId, string updatedBy, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@submitted_by_user_id", submittedByUserId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@outcome", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_acknowledge_upd",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var outcome = parameters.Get<int>("@outcome");

        return outcome switch
        {
            0 => AcknowledgeTaskOutcome.Acknowledged,
            1 => AcknowledgeTaskOutcome.NotFound,
            2 => AcknowledgeTaskOutcome.NotTerminal,
            _ => throw new InvalidOperationException(
                $"Unrecognized @outcome '{outcome}' returned by dbo.task_queue_acknowledge_upd.")
        };
    }

    /// <summary>
    /// CAS-037 §5/§7 V1 — calls <c>dbo.task_queue_active_by_type_get</c> and maps its single-row,
    /// single-column (<c>is_active_task</c> BIT) result.
    /// </summary>
    public async Task<bool> HasActiveTaskOfTypeAsync(string taskType, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_type", taskType);

        await using var connection = new SqlConnection(_connectionString);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "dbo.task_queue_active_by_type_get",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    /// <summary>
    /// CAS-056 §7 — calls <c>dbo.task_queue_cancel_req</c> and maps its single-row <c>outcome</c>
    /// (<c>0</c> flag set, <c>1</c> Queued cancelled, <c>2</c> not found/not owned, <c>3</c> not
    /// cancellable). Ownership, state and the audit row are handled inside the procedure.
    /// </summary>
    public async Task<CancelTaskResult> CancelAsync(
        int taskQueueId, int requestingUserId, string updatedBy, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);
        parameters.Add("@requesting_user_id", requestingUserId);
        parameters.Add("@updated_by", updatedBy);

        await using var connection = new SqlConnection(_connectionString);

        var outcome = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.task_queue_cancel_req",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return new CancelTaskResult
        {
            Outcome = outcome switch
            {
                0 => CancelTaskOutcome.CancelRequested,
                1 => CancelTaskOutcome.Cancelled,
                2 => CancelTaskOutcome.NotFound,
                3 => CancelTaskOutcome.NotCancellable,
                _ => throw new InvalidOperationException(
                    $"Unrecognized outcome '{outcome}' returned by dbo.task_queue_cancel_req.")
            }
        };
    }

    /// <summary>
    /// CAS-056 §7 — calls <c>dbo.task_queue_cancel_requested_get</c> (single-row, single-column
    /// <c>is_cancel_requested</c> BIT); <c>false</c> when the row does not exist.
    /// </summary>
    public async Task<bool> IsCancelRequestedAsync(int taskQueueId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", taskQueueId);

        await using var connection = new SqlConnection(_connectionString);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "dbo.task_queue_cancel_requested_get",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    private sealed record ListRow(
        int task_queue_id,
        string task_type,
        int submitted_by_user_id,
        string username,
        DateTime submitted_at,
        DateTime? started_at,
        DateTime? completed_at,
        string status,
        DateTime? result_acknowledged_at);

    private sealed record SummaryRow(
        int active_count,
        int unacknowledged_terminal_count);

    // CAS-054 SC-10: a settable class (not a positional record) so Dapper tolerates a claim result
    // set that lacks the newer columns (old DB + new Api/Worker) - they simply stay null.
    private sealed class ClaimRow
    {
        public int task_queue_id { get; set; }
        public string task_type { get; set; } = string.Empty;
        public int submitted_by_user_id { get; set; }
        public string? request_parameters { get; set; }
        public DateTime submitted_at { get; set; }
        public int? resume_watermark_file_index_id { get; set; }
        public int? restarted_from_task_queue_id { get; set; }
    }

    private sealed record GetRow(
        int task_queue_id,
        string task_type,
        string status,
        int submitted_by_user_id,
        DateTime submitted_at,
        DateTime? started_at,
        DateTime? completed_at,
        string? request_parameters,
        string? result_detail,
        int? items_processed,
        int? items_total,
        DateTime? progress_updated_at,
        DateTime? result_acknowledged_at);

    private sealed record SweptRow(
        int task_queue_id,
        string task_type,
        int submitted_by_user_id,
        int? resumed_as_task_queue_id,
        bool auto_resume_suppressed,
        int? carried_over_count);
}

