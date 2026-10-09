namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /quarantine/delete</c> request body (CAS-008 §6.4, FR-16/FR-17). <see cref="FileIds"/>
/// are <c>quarantine_entry_id</c> values, as returned by <c>GET /quarantine</c> — not the
/// <c>search_result_id</c> values used at add-time. <see cref="Confirm"/> must be <c>true</c>;
/// missing/<c>false</c> rejects the entire request before any deletion is attempted (§7 V2).
/// </summary>
public sealed record QuarantineDeleteRequest
{
    public required IReadOnlyList<int> FileIds { get; init; }

    public bool Confirm { get; init; }
}
