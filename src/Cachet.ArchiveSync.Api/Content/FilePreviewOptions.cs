using System.ComponentModel.DataAnnotations;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// CAS-040 §9 — File Viewer preview caps, bound from <c>Files:Preview</c> and validated on start
/// (same pattern as Web's <c>TaskPollingOptions</c>). Defaults are the §9 engineering proposals.
/// Holds no secret and no B2 setting.
/// </summary>
public sealed class FilePreviewOptions
{
    public const string SectionName = "Files:Preview";

    /// <summary>Above this, a PDF is <c>DownloadOnly</c> (<c>TooLarge</c>).</summary>
    [Range(1, long.MaxValue)]
    public long PdfMaxBytes { get; set; } = 52_428_800;

    /// <summary>TXT / DOC-extract previews are truncated here.</summary>
    [Range(1, int.MaxValue)]
    public int TextMaxBytes { get; set; } = 2_097_152;

    /// <summary>Above this, DOC/DOCX/XLS/XLSX are <c>DownloadOnly</c> (<c>TooLarge</c>).</summary>
    [Range(1, int.MaxValue)]
    public int OfficeMaxBytes { get; set; } = 20_971_520;

    [Range(1, int.MaxValue)]
    public int MaxSheets { get; set; } = 20;

    [Range(1, int.MaxValue)]
    public int MaxRowsPerSheet { get; set; } = 1000;

    [Range(1, int.MaxValue)]
    public int MaxColumns { get; set; } = 50;

    /// <summary>Rendered-HTML cap for DOCX previews (truncation banner).</summary>
    [Range(1, int.MaxValue)]
    public int MaxHtmlBytes { get; set; } = 5_242_880;

    /// <summary>OOXML archive guard — total declared uncompressed size.</summary>
    [Range(1, long.MaxValue)]
    public long MaxUncompressedBytes { get; set; } = 209_715_200;

    /// <summary>OOXML archive guard — per-entry uncompressed/compressed ratio.</summary>
    [Range(1, int.MaxValue)]
    public int MaxCompressionRatio { get; set; } = 100;

    /// <summary>Conversion timeout (V9 <c>Timeout</c>).</summary>
    [Range(1, int.MaxValue)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary><c>SemaphoreSlim</c> bound on concurrent conversions.</summary>
    [Range(1, int.MaxValue)]
    public int MaxConcurrentConversions { get; set; } = 2;
}
