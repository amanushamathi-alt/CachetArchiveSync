namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Result of <c>b2_download_file_by_id</c> (CAS-001 §6.2 step 4). A class, not a record — it owns
/// a live <see cref="Stream"/> resource, so identity/disposal semantics matter more than value
/// equality. The caller is responsible for disposing this (which disposes <see cref="Content"/>).
/// </summary>
public sealed class B2DownloadedFile : IDisposable
{
    public required string FileName { get; init; }

    public required string ContentSha1 { get; init; }

    public required long ContentLength { get; init; }

    public required Stream Content { get; init; }

    public void Dispose() => Content.Dispose();
}
