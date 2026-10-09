namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One per-file outcome of <c>POST /quarantine/remove</c> (CAS-014 §8). Every requested file is
/// attempted independently — a failure on one file never blocks another (§9 V2, §10 SC-3).
/// </summary>
public sealed record QuarantineRemoveResultItem
{
    public required int FileId { get; init; }

    /// <summary><c>"Success"</c> or <c>"Failed"</c>.</summary>
    public required string Result { get; init; }

    /// <summary>Populated only when <see cref="Result"/> is <c>"Failed"</c>.</summary>
    public string? Reason { get; init; }
}
