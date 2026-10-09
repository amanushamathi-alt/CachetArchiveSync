namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary><c>POST /quarantine/browse</c> <c>200 OK</c> body (CAS-050 §8). Counts only — no paths or content.</summary>
public sealed record QuarantineBrowseAddResponse
{
    public required int Added { get; init; }

    public required int AlreadyQuarantined { get; init; }
}
