namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>One page of a <c>b2_list_file_names</c> call (CAS-001 §6.2 step 2).</summary>
public sealed record B2ListFileNamesResult
{
    public required IReadOnlyList<B2FileInfo> Files { get; init; }

    /// <summary>Pass as the next call's <c>startFileName</c> to continue paging; null when this is the last page.</summary>
    public string? NextFileName { get; init; }
}
