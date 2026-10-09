using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// <c>GET /quarantine</c> read path (CAS-008 §6, FR-14) — split from <see cref="IQuarantineService"/>
/// along the same responsibility/security-posture seam CAS-007 already used to split
/// <c>ISyncScanService</c>/<c>ISyncActionService</c> (Karpathy P4, pattern now appeared twice):
/// listing is a plain <c>CachetAuthenticated</c> read, add/delete are distinct write paths with
/// their own auth policies.
/// </summary>
public interface IQuarantineListService
{
    /// <summary>CAS-050 §8 — one page; the caller has already validated <paramref name="page"/> (&gt;= 1) and
    /// <paramref name="pageSize"/> (25 or 50).</summary>
    Task<QuarantineListResponse> GetActiveAsync(int actingUserId, int page, int pageSize, CancellationToken ct);
}
