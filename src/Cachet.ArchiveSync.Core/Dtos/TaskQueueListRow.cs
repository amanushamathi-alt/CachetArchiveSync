namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One row of <c>dbo.task_queue_lst</c> (CAS-026 §5) — a <c>dbo.task_queue</c> row joined to
/// <c>dbo.users</c> for the display <c>submitted_by</c> username. Backs <c>GET /tasks</c> (§6.1,
/// FR-19). Public in <c>Core</c> (not a repository-private row type), matching
/// <see cref="AuditLogRow"/>'s precedent, because the service layer needs it to project into
/// <see cref="TaskListItem"/>.
/// </summary>
public sealed record TaskQueueListRow
{
    public required int TaskQueueId { get; init; }

    public required string TaskType { get; init; }

    //public required string SubmittedBy { get; init; }

    public required int SubmittedByUserId { get; init; }

    public required string Username { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ResultAcknowledgedAt { get; init; }
}
