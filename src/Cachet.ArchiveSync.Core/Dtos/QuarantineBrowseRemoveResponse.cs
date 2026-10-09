namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary><c>POST /quarantine/browse/remove</c> <c>200 OK</c> body (CAS-050 §8).</summary>
public sealed record QuarantineBrowseRemoveResponse
{
    public required int Removed { get; init; }
}
