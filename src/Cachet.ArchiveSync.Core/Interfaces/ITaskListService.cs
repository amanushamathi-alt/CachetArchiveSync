using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="ITaskListService.ListAsync"/> (CAS-026 §6.1/§7).</summary>
public enum TaskListOutcome
{
    Found,

    /// <summary><c>status</c>/<c>page</c>/<c>pageSize</c> failed §7 V1–V3 — <c>422 validation-failed</c>.</summary>
    ValidationFailed
}

/// <summary>Result of one <see cref="ITaskListService.ListAsync"/> call.</summary>
public sealed record TaskListResult
{
    public required TaskListOutcome Outcome { get; init; }

    public PagedResult<TaskListItem>? Response { get; init; }

    /// <summary>Populated only when <see cref="Outcome"/> is <see cref="TaskListOutcome.ValidationFailed"/>.</summary>
    public string? ValidationError { get; init; }
}

/// <summary>
/// <c>GET /tasks</c> read path (CAS-026 §6.1, FR-19) — the authenticated user's own tasks,
/// newest-first by <c>submitted_at</c>, optionally filtered by status, paginated. Not audited (§12
/// — a plain read, matching <see cref="IAuditLogListService"/>'s/<see cref="IQuarantineListService"/>'s
/// precedent). Ownership is enforced server-side (<paramref name="actingUserId"/> bound to the
/// authenticated caller's own claim, never client-supplied, §7 V6).
/// </summary>
public interface ITaskListService
{
    Task<TaskListResult> ListAsync(
        string? status,
        int page,
        int pageSize,
        int actingUserId,
        CancellationToken ct);
}
