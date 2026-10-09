namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>202 Accepted</c> response body shared by <c>POST /sync/scan</c>,
/// <c>POST /sync/actions/copy</c>, <c>POST /sync/actions/update</c>, and
/// <c>POST /sync/actions/purge</c> (CAS-007 §6) — all four submissions return the identical
/// <c>taskId</c>/<c>type</c>/<c>status</c>/<c>submittedAt</c> shape, only <see cref="Type"/>
/// varying (<c>ContentScan</c>/<c>SyncCopy</c>/<c>SyncUpdate</c>/<c>SyncPurge</c>). One shared
/// record for this spec's own four endpoints (not reusing CAS-006's <c>SearchSubmittedResponse</c>,
/// which is a different module's contract) per Karpathy P4 — the pattern already appears four
/// times within this single spec.
/// </summary>
public sealed record TaskSubmittedResponse
{
    public required int TaskId { get; init; }

    public required string Type { get; init; }

    /// <summary>Always <c>"Queued"</c> at submission time.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}
