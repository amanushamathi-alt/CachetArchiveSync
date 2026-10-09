namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Read-side projection of one <c>dbo.task_queue</c> row, keyed by <c>task_queue_id</c>.
///
/// BLOCKED (implementer, CAS-006): no stored procedure to read a single <c>dbo.task_queue</c> row
/// by id was delivered in
/// <c>Database/migrations/20260922_170000__CAS-006-keyword-search-api.sql</c> (only
/// <c>task_queue_ins</c>/<c>task_queue_claim</c>/<c>task_queue_upd</c> exist — all write paths,
/// no read-by-id). <c>GET /search/{taskId}/results</c> (§6.2, §7 V4/V5) and
/// <c>POST /search/results/quarantine</c>'s <c>404 task-not-found</c> case (§6.3) both require
/// exactly this lookup. <see cref="Interfaces.ITaskQueueRepository.GetAsync"/> is implemented
/// against a stored procedure named <c>dbo.task_queue_get</c>, following this project's existing
/// <c>schema.entity_action</c> naming convention and returning exactly the columns below — but
/// that procedure does not exist yet. This is a database-layer gap for db-author to close, not a
/// spec-silence issue (spec text assumes this lookup is possible); flagged here rather than
/// worked around with ad-hoc (non-stored-procedure) SQL, which would break this project's
/// Dapper-via-stored-procedure-only convention used by every other repository in this codebase.
/// </summary>
public sealed record TaskQueueRow
{
    public required int TaskQueueId { get; init; }

    public required string TaskType { get; init; }

    public required string Status { get; init; }

    public required int SubmittedByUserId { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? RequestParameters { get; init; }

    public string? ResultDetail { get; init; }

    /// <summary>
    /// CAS-016 §2/§7 — the original <c>Failed</c> row's id if this row was created by a restart
    /// (Model B); <c>null</c> for every normally-submitted row. Populated once
    /// <c>dbo.task_queue_get</c> exists (still BLOCKED, see this file's class-level XML doc) — the
    /// column is added here now so this DTO compiles against the new
    /// <c>restarted_from_task_queue_id</c> schema column (Karpathy P5, build incrementally).
    /// </summary>
    public int? RestartedFromTaskQueueId { get; init; }

    /// <summary>
    /// CAS-029 §3.5 — units of work finished so far, as last written by the Worker via
    /// <c>dbo.task_queue_progress_upd</c>. <c>null</c> until the job's first progress write.
    /// Measured, never estimated; not exposed by any endpoint in CAS-029 (§4/§6).
    /// </summary>
    public int? ItemsProcessed { get; init; }

    /// <summary>CAS-029 §3.5 — units of work counted at job start; <c>null</c> until then.</summary>
    public int? ItemsTotal { get; init; }

    /// <summary>CAS-029 §3.5 — UTC time of the last successful progress write; <c>null</c> until then.</summary>
    public DateTimeOffset? ProgressUpdatedAt { get; init; }

    /// <summary>
    /// CAS-030 §5 — UTC time the submitting user first viewed this task's finished outcome;
    /// <c>null</c> = not yet viewed (still counts toward the Task Manager badge).
    /// </summary>
    public DateTimeOffset? ResultAcknowledgedAt { get; init; }
}
