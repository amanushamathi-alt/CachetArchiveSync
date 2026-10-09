using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// <c>GET /tasks/summary</c> / <c>POST /tasks/mark-viewed</c> (CAS-026 §6.3/§6.4, FR-22) — the
/// Task Manager header badge's two counts and the account-level "last viewed" reset that clears
/// them. Neither call is audited (§12): the summary is a plain read, and mark-viewed mutates only a
/// per-user UI-state bookkeeping timestamp with no file/task/security consequence — it fits none of
/// FR-23's four audited action categories. Both are scoped to <paramref name="actingUserId"/>'s own
/// row only, bound to the authenticated caller's own claim, never client-supplied (§7 V8).
/// </summary>
public interface ITaskBadgeService
{
    Task<TaskSummaryResponse> GetSummaryAsync(int actingUserId, CancellationToken ct);

    Task<MarkViewedResponse> MarkViewedAsync(int actingUserId, CancellationToken ct);
}
