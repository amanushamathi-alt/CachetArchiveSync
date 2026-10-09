namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One per-file outcome of <c>POST /quarantine/delete</c> (CAS-008 §6.4). Every requested file is
/// attempted independently — a failure on one file never blocks or rolls back another (§6, §7
/// V3-V6).
/// </summary>
public sealed record QuarantineDeleteResultItem
{
    public required int FileId { get; init; }

    /// <summary><c>"Success"</c> or <c>"Failed"</c>.</summary>
    public required string Result { get; init; }

    /// <summary>Populated only when <see cref="Result"/> is <c>"Failed"</c>.</summary>
    public string? Reason { get; init; }
}
