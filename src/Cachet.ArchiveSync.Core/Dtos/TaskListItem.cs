namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One item of <c>GET /tasks</c>'s <c>200 OK</c> <c>items</c> array (CAS-026 §6.1, FR-19).
/// <see cref="StartTime"/> maps to <c>submitted_at</c>, not <c>started_at</c> — CAS-026 §5's design
/// note: a <c>Queued</c> row has no <c>started_at</c> yet, and the API design doc's worked example
/// uses <c>submitted_at</c>'s value for <c>startTime</c>.
/// </summary>
public sealed record TaskListItem
{
    public required int TaskId { get; init; }

    public required string Type { get; init; }

    public required string SubmittedBy { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required string Status { get; init; }

    public required bool IsAcknowledged { get; init; }

    /// <summary>CAS-047 §2/§8: the search mode of a <c>KeywordSearch</c> task; null for legacy and non-search tasks.</summary>
    public string? SearchMode { get; init; }
}
