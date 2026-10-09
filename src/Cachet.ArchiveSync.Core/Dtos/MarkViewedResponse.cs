namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /tasks/mark-viewed</c>'s <c>200 OK</c> response body (CAS-026 §6.4, FR-22).
/// </summary>
public sealed record MarkViewedResponse
{
    public required DateTimeOffset MarkedViewedAt { get; init; }
}
