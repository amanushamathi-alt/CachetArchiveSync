namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary><c>POST /quarantine/remove</c>'s <c>200 OK</c> response body (CAS-014 §8).</summary>
public sealed record QuarantineRemoveResponse
{
    public required IReadOnlyList<QuarantineRemoveResultItem> Results { get; init; }
}
