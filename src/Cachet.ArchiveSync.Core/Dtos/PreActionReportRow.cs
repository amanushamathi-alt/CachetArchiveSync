namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One row of the Pre-Action Impact Report (CAS-010 §3/§6.1) — either a <c>dbo.search_result</c>
/// row (<see cref="Panel"/> = "Filename Matches"/"Content Matches") or an <c>Active</c>
/// <c>dbo.quarantine_entry</c> row (<see cref="Panel"/> = "Quarantine"). Per §3's resolved dedup
/// decision, a file that is both an active search match and actively quarantined appears as two
/// separate rows — this type carries no identity linking the two.
/// </summary>
public sealed record PreActionReportRow
{
    public required string FileName { get; init; }

    public required string FilePath { get; init; }

    public required string Panel { get; init; }

    public required string Status { get; init; }
}
