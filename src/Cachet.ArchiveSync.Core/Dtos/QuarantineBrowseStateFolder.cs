namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>One direct-child folder of <see cref="QuarantineBrowseStateResponse"/> (CAS-050 §8) — counts cover all depths.</summary>
public sealed record QuarantineBrowseStateFolder
{
    public required string FolderName { get; init; }

    public required int TotalFiles { get; init; }

    public required int QuarantinedFiles { get; init; }
}
