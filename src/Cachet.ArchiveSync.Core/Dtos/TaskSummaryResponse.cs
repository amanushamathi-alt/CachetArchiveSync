namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /tasks/summary</c>'s <c>200 OK</c> response body (CAS-026 §6.3, FR-22) — the Task
/// Manager header badge's two counts. Zero counts is a normal, not-error response.
/// </summary>
public sealed record TaskSummaryResponse
{
    public required int ActiveCount { get; init; }

    public required int UnacknowledgedTerminalCount { get; init; }
}
