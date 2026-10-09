namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Typed payload serialized into <c>dbo.task_queue.request_parameters</c> for
/// <c>task_type = 'KeywordSearch'</c> (CAS-006 §3, per CAS-004 §5's resolved
/// action/result-catalog convention). Deliberately separate from <see cref="SearchRequest"/> (the
/// HTTP contract) even though the fields are identical today — the persisted task-queue payload
/// contract and the public API request contract are allowed to evolve independently.
/// Serialized/deserialized with <c>System.Text.Json</c> at the repository boundary only.
/// <see cref="FromDate"/> / <see cref="ToDate"/> (CAS-038 §2) are the optional <c>created_at</c>
/// range — both null for keyword-only tasks and for pre-CAS-038 payloads that lack the keys.
/// </summary>
public sealed record KeywordSearchRequestDto
{
    public required string Term { get; init; }

    public required string Bucket { get; init; }

    public required string Extension { get; init; }

    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    /// <summary>CAS-047 §2: <c>Filename</c>, <c>Keyword</c> or <c>Date</c>; null for legacy payloads (V6).</summary>
    public string? Mode { get; init; }
}
