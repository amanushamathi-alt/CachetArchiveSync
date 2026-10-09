namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>One page of a <c>b2_list_file_versions</c> call (CAS-001 §6.4 Step B).</summary>
public sealed record B2ListFileVersionsResult
{
    public required IReadOnlyList<B2FileInfo> Files { get; init; }

    public string? NextFileName { get; init; }

    public string? NextFileId { get; init; }
}
