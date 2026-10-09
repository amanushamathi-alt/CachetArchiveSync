namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /files/{fileIndexId}?context=</c> 200 body (CAS-040 §6.1). Never carries a path —
/// relative or absolute (the relative path is already on screen in the originating list).
/// </summary>
public sealed record FileMetadataResponse
{
    public required int FileIndexId { get; init; }

    public required string Name { get; init; }

    /// <summary>Lower-cased, without the leading dot.</summary>
    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    /// <summary><c>ODrive</c> or <c>B2</c>.</summary>
    public required string Source { get; init; }

    /// <summary>One of <see cref="FileViewModes"/>.</summary>
    public required string ViewMode { get; init; }

    /// <summary><c>TooLarge</c>, <c>SignatureMismatch</c>, or <c>null</c>.</summary>
    public string? DownloadOnlyReason { get; init; }

    public required bool PreviewTruncated { get; init; }
}

/// <summary>CAS-040 §6.1 <c>viewMode</c> literals — shared by Api (producer) and Web (consumer).</summary>
public static class FileViewModes
{
    public const string InlinePdf = "InlinePdf";
    public const string InlineText = "InlineText";
    public const string ExtractedText = "ExtractedText";
    public const string HtmlPreview = "HtmlPreview";
    public const string DownloadOnly = "DownloadOnly";
}
