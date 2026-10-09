namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary><c>POST /quarantine/delete</c>'s <c>200 OK</c> response body (CAS-008 §6.4).</summary>
public sealed record QuarantineDeleteResponse
{
    public required IReadOnlyList<QuarantineDeleteResultItem> Results { get; init; }
}
