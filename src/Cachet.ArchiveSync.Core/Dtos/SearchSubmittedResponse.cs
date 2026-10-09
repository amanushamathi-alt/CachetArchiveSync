namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary><c>POST /search</c> <c>202 Accepted</c> response body (CAS-006 §6).</summary>
public sealed record SearchSubmittedResponse
{
    public required int TaskId { get; init; }

    /// <summary>Always <c>"KeywordSearch"</c> for this endpoint.</summary>
    public required string Type { get; init; }

    /// <summary>Always <c>"Queued"</c> at submission time.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}
