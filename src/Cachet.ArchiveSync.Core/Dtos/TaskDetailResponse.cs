namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /tasks/{taskId}</c>'s <c>200 OK</c> response body (CAS-026 §6.2, FR-19).
/// <see cref="ResultDetail"/> reuses <c>dbo.task_queue.result_detail</c> — the same value the audit
/// log's <c>result</c>/<c>detail</c> carries for the same event, no separate error-message store
/// (UI-UX §5.2) — and is populated only when <see cref="Status"/> is <c>Failed</c> (§10 SC-3).
/// </summary>
public sealed record TaskDetailResponse
{
    public required int TaskId { get; init; }

    public required string Type { get; init; }

    public required string SubmittedBy { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required string Status { get; init; }

    public required bool IsAcknowledged { get; init; }

    /// <summary>CAS-029 measured progress counts (<c>task_queue.items_processed</c>); null until the Worker reports.</summary>
    public int? ItemsProcessed { get; init; }

    /// <summary>CAS-029 measured total (<c>task_queue.items_total</c>); null until the Worker reports.</summary>
    public int? ItemsTotal { get; init; }

    /// <summary><c>task_queue.result_detail</c> as is — only when <see cref="Status"/> is <c>Failed</c>.</summary>
    public string? ResultDetail { get; init; }

    /// <summary>Fixed projection (term, extension, optional CAS-038 date range) — only when <see cref="Type"/> is <c>KeywordSearch</c>.</summary>
    public TaskSearchParameters? SearchParameters { get; init; }
}

/// <summary>
/// CAS-030 §6/§12 — the fixed, data-minimized projection of a <c>KeywordSearch</c> task's
/// <c>request_parameters</c>: <c>term</c> and <c>extension</c>, plus the optional
/// <c>fromDate</c>/<c>toDate</c> range deliberately added by CAS-038 §2.7/§11 so reloaded results
/// are not misrepresented as unfiltered (null for keyword-only and pre-CAS-038 tasks). Never
/// <c>bucket</c> or any other future field.
/// </summary>
public sealed record TaskSearchParameters
{
    public required string Term { get; init; }

    public required string Extension { get; init; }

    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    /// <summary>CAS-047 §8: search mode; null for legacy tasks.</summary>
    public string? Mode { get; init; }
}
