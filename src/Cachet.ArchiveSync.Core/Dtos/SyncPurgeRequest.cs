namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /sync/actions/purge</c> request body (CAS-007 §6). <see cref="Confirm"/> must be
/// <c>true</c> — same server-side backstop as Permanent Delete (§7 V7); absent/<c>false</c> is
/// rejected before any task is queued.
/// </summary>
public sealed record SyncPurgeRequest
{
    public required int TaskId { get; init; }

    public required IReadOnlyList<int> FileIds { get; init; }

    public bool Confirm { get; init; }
}
