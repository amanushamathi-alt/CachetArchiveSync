namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>One page of <c>dbo.quarantine_entry_lst</c> plus its <c>@total_count</c> of all Active rows (CAS-050 §7).</summary>
public sealed record QuarantineEntryListPage
{
    public required IReadOnlyList<QuarantineEntryListRow> Rows { get; init; }

    public required int TotalCount { get; init; }
}
