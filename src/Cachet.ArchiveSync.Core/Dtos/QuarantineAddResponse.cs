namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /search/results/quarantine</c> <c>200 OK</c> response body (CAS-006 §6.3). Returns
/// only the newly added row(s), not the full Quarantine list (no <c>GET /quarantine</c> in this
/// spec — §0/§4). <see cref="Warnings"/> names any requested <c>fileId</c>s that were skipped
/// (e.g. already actively quarantined) and why (§7 V8) — present only on a partial-success
/// response.
/// </summary>
public sealed record QuarantineAddResponse
{
    public required IReadOnlyList<QuarantineAddResponseItem> Items { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }
}
