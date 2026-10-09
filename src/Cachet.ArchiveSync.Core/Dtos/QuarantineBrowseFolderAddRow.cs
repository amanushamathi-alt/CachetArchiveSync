namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>Counts from <c>dbo.quarantine_entry_ins_browse_folder</c>'s OUTPUT parameters (CAS-050 §7).</summary>
public sealed record QuarantineBrowseFolderAddRow
{
    public required int Added { get; init; }

    public required int AlreadyQuarantined { get; init; }
}
