namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Body of <c>POST /quarantine/browse</c> and <c>POST /quarantine/browse/remove</c> (CAS-050 §8, FR-09a).
/// <see cref="Scope"/> is <c>File</c> (needs <see cref="FileIndexId"/>) or <c>Folder</c> (needs
/// <see cref="Source"/>, <see cref="BucketName"/> and a non-empty <see cref="FolderPath"/> with no
/// leading/trailing <c>/</c>) — validated by the service (§9 V1).
/// </summary>
public sealed record QuarantineBrowseRequest
{
    public string? Scope { get; init; }

    public int? FileIndexId { get; init; }

    public string? Source { get; init; }

    public string? BucketName { get; init; }

    public string? FolderPath { get; init; }
}
