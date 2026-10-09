using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IFileContentService"/> implementation of CAS-040 §6 — the single resolution pipeline
/// behind the three <c>/files/{fileIndexId}</c> endpoints. The client supplies only an int id; the
/// path is built from DB values by <see cref="IMirrorFileReader.OpenReadAsync"/> (§12). No B2
/// client, credential or presigned URL is a dependency here (SC-12). <c>/view</c> and
/// <c>/download</c> await <see cref="IAuditLogService.WriteAsync"/> before returning any result —
/// success or failure — so the controller never writes a header or byte ahead of the audit row
/// (V10, SC-9).
/// </summary>
public sealed class FileContentService : IFileContentService
{
    private const string ActingProcessIdentity = "api-svc";

    private const string MetadataOperation = "metadata";
    private const string ViewOperation = "view";
    private const string DownloadOperation = "download";

    private const string PlainTextUtf8 = "text/plain; charset=utf-8";
    private const string HtmlUtf8 = "text/html; charset=utf-8";
    private const string PdfContentType = "application/pdf";

    // V9 reasons.
    private const string TooLarge = "TooLarge";
    private const string SignatureMismatch = "SignatureMismatch";
    private const string ConversionFailed = "ConversionFailed";
    private const string Timeout = "Timeout";
    private const string ArchiveGuard = "ArchiveGuard";

    private const string FileBrowserContext = "FileBrowser";

    // §6 — context → dbo.audit_log.tab_module (case-sensitive, V2).
    private static readonly Dictionary<string, string> ContextTabModules = new(StringComparer.Ordinal)
    {
        ["KeywordSearch"] = "Keyword Search",
        ["ODriveSync"] = "O-Drive Sync",
        ["Quarantine"] = "Quarantine",
        // CAS-043 §3 — Files screen opens are audited under the existing Keyword Search module.
        [FileBrowserContext] = "Keyword Search"
    };

    // §8.1 — the extension allowlist (V5) and its download Content-Type column. Content-Type is
    // chosen from the extension, never sniffed (§12).
    private static readonly Dictionary<string, string> DownloadContentTypes = new(StringComparer.Ordinal)
    {
        ["pdf"] = PdfContentType,
        ["txt"] = "text/plain",
        ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    // V9 signatures.
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipSignature = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] Ole2Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly IMirrorFileReader _mirrorFileReader;
    private readonly IAuditLogService _auditLogService;
    private readonly IOfficePreviewRenderer _officePreviewRenderer;
    private readonly PreviewConversionLimiter _conversionLimiter;
    private readonly FilePreviewOptions _options;
    private readonly ILogger<FileContentService> _logger;

    public FileContentService(
        IFileIndexRepository fileIndexRepository,
        IMirrorFileReader mirrorFileReader,
        IAuditLogService auditLogService,
        IOfficePreviewRenderer officePreviewRenderer,
        PreviewConversionLimiter conversionLimiter,
        IOptions<FilePreviewOptions> options,
        ILogger<FileContentService> logger)
    {
        _fileIndexRepository = fileIndexRepository;
        _mirrorFileReader = mirrorFileReader;
        _auditLogService = auditLogService;
        _officePreviewRenderer = officePreviewRenderer;
        _conversionLimiter = conversionLimiter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FileAccessResult> GetMetadataAsync(int fileIndexId, string? context, CancellationToken ct)
    {
        if (!TryMapContext(context, out _))
        {
            return RejectContext(MetadataOperation, fileIndexId);
        }

        var stopwatch = Stopwatch.StartNew();
        var resolution = await ResolveAsync(fileIndexId, MetadataOperation, ct);
        if (resolution.Outcome != FileContentOutcome.Success)
        {
            return Outcome(resolution.Outcome);
        }

        await using var handle = resolution.Handle!;
        var record = resolution.Record!;
        var (viewMode, downloadOnlyReason) = await ComputeViewModeAsync(resolution.Extension!, handle, ct);

        _logger.LogInformation(
            "File {Operation} served for {FileIndexId} ({Extension}, {SizeBytes} bytes, {ViewMode}) in {ElapsedMs} ms",
            MetadataOperation, fileIndexId, resolution.Extension, handle.Length, viewMode, stopwatch.ElapsedMilliseconds);

        return new FileAccessResult
        {
            Outcome = FileContentOutcome.Success,
            Metadata = new FileMetadataResponse
            {
                FileIndexId = record.FileIndexId,
                Name = record.FileName,
                Extension = resolution.Extension!,
                SizeBytes = handle.Length,
                ModifiedAt = record.ModifiedAt,
                Source = record.Source,
                ViewMode = viewMode,
                DownloadOnlyReason = downloadOnlyReason,
                // Known before rendering only for TXT (size vs. TextMaxBytes). DOC-extract and
                // office-HTML truncation is signalled by the /view body itself (in-document banner)
                // and its X-Cachet-Preview-Truncated header.
                PreviewTruncated = viewMode == FileViewModes.InlineText && handle.Length > _options.TextMaxBytes
            }
        };
    }

    public async Task<FileAccessResult> GetViewAsync(int fileIndexId, string? context, int actingUserId, CancellationToken ct)
    {
        if (!TryMapContext(context, out var tabModule))
        {
            return RejectContext(ViewOperation, fileIndexId);
        }

        var stopwatch = Stopwatch.StartNew();
        var resolution = await ResolveAsync(fileIndexId, ViewOperation, ct);
        if (resolution.Outcome != FileContentOutcome.Success)
        {
            return await AuditFailureAsync(
                AuditAction.FileViewed, actingUserId, tabModule, resolution.Record, fileIndexId,
                resolution.Outcome, ReasonFor(resolution.Outcome), previewReason: null, context, ct);
        }

        var handle = resolution.Handle!;
        var record = resolution.Record!;
        var extension = resolution.Extension!;
        var transferred = false;
        try
        {
            var (viewMode, downloadOnlyReason) = await ComputeViewModeAsync(extension, handle, ct);
            if (viewMode == FileViewModes.DownloadOnly)
            {
                return await RefusePreviewAsync(actingUserId, tabModule, record, downloadOnlyReason!, context, ct);
            }

            Stream content;
            string contentType;
            bool? previewTruncated = null;
            string? sourceEncoding = null;

            switch (viewMode)
            {
                case FileViewModes.InlinePdf:
                    // Original bytes, streamed — the handle's stream is handed to the response.
                    content = handle.Stream!;
                    contentType = PdfContentType;
                    break;

                case FileViewModes.InlineText:
                    {
                        var truncated = handle.Length > _options.TextMaxBytes;
                        var capped = await ReadUpToAsync(handle, (int)Math.Min(handle.Length, _options.TextMaxBytes), ct);
                        var (text, encoding) = TextPreviewDecoder.Decode(capped, truncated);
                        content = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
                        contentType = PlainTextUtf8;
                        previewTruncated = truncated;
                        sourceEncoding = encoding;
                        break;
                    }

                default:
                    {
                        // ExtractedText (doc) or HtmlPreview (docx/xls/xlsx). Size is already under
                        // OfficeMaxBytes (DownloadOnly otherwise).
                        var bytes = await ReadUpToAsync(handle, (int)handle.Length, ct);

                        if (extension is "docx" or "xlsx")
                        {
                            var verdict = OoxmlArchiveGuard.Check(bytes, _options.MaxUncompressedBytes, _options.MaxCompressionRatio);
                            if (verdict != OoxmlArchiveGuard.Verdict.Pass)
                            {
                                return await RefusePreviewAsync(
                                    actingUserId, tabModule, record,
                                    verdict == OoxmlArchiveGuard.Verdict.Rejected ? ArchiveGuard : ConversionFailed, context, ct);
                            }
                        }

                        Func<OfficePreview> work = extension switch
                        {
                            "doc" => () => _officePreviewRenderer.ExtractLegacyDocText(bytes),
                            "docx" => () => _officePreviewRenderer.RenderDocx(bytes),
                            _ => () => _officePreviewRenderer.RenderSpreadsheet(bytes, isXlsx: extension == "xlsx")
                        };

                        var (status, preview) = await _conversionLimiter.RunAsync(work, ct);
                        if (status != PreviewConversionLimiter.Status.Completed || preview is null)
                        {
                            return await RefusePreviewAsync(
                                actingUserId, tabModule, record,
                                status == PreviewConversionLimiter.Status.Timeout ? Timeout : ConversionFailed, context, ct);
                        }

                        content = new MemoryStream(preview.Body, writable: false);
                        contentType = viewMode == FileViewModes.ExtractedText ? PlainTextUtf8 : HtmlUtf8;
                        previewTruncated = preview.Truncated;
                        break;
                    }
            }

            // V10 — audit before any response byte; on failure no content is returned.
            var audited = await TryAuditAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = actingUserId,
                TabModule = tabModule,
                Action = AuditAction.FileViewed,
                FilePath = record.RelativePath,
                TaskQueueId = null,
                Result = AuditResult.Success,
                ResultDetail = WithContext(SuccessDetail(record, viewMode, handle.Length), context)
            }, ct);

            if (!audited)
            {
                await content.DisposeAsync();
                LogRefused(ViewOperation, fileIndexId, nameof(FileContentOutcome.AuditWriteFailed));
                return Outcome(FileContentOutcome.AuditWriteFailed);
            }

            _logger.LogInformation(
                "File {Operation} served for {FileIndexId} ({Extension}, {SizeBytes} bytes, {ViewMode}) in {ElapsedMs} ms",
                ViewOperation, fileIndexId, extension, handle.Length, viewMode, stopwatch.ElapsedMilliseconds);

            // Only an InlinePdf body is the handle's own stream; every other body is a rendered
            // copy, so the handle is released in the finally block below.
            transferred = viewMode == FileViewModes.InlinePdf;
            return new FileAccessResult
            {
                Outcome = FileContentOutcome.Success,
                Content = content,
                ContentType = contentType,
                FileName = record.FileName,
                ViewMode = viewMode,
                PreviewTruncated = previewTruncated,
                SourceEncoding = sourceEncoding
            };
        }
        finally
        {
            if (!transferred)
            {
                await handle.DisposeAsync();
            }
        }
    }

    public async Task<FileAccessResult> GetDownloadAsync(int fileIndexId, string? context, int actingUserId, CancellationToken ct)
    {
        if (!TryMapContext(context, out var tabModule))
        {
            return RejectContext(DownloadOperation, fileIndexId);
        }

        var stopwatch = Stopwatch.StartNew();
        var resolution = await ResolveAsync(fileIndexId, DownloadOperation, ct);
        if (resolution.Outcome != FileContentOutcome.Success)
        {
            return await AuditFailureAsync(
                AuditAction.FileDownloaded, actingUserId, tabModule, resolution.Record, fileIndexId,
                resolution.Outcome, ReasonFor(resolution.Outcome), previewReason: null, context, ct);
        }

        var handle = resolution.Handle!;
        var record = resolution.Record!;

        // V10 — audit before any response byte.
        bool audited;
        try
        {
            audited = await TryAuditAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = actingUserId,
                TabModule = tabModule,
                Action = AuditAction.FileDownloaded,
                FilePath = record.RelativePath,
                TaskQueueId = null,
                Result = AuditResult.Success,
                ResultDetail = WithContext(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"fileIndexId={record.FileIndexId}; source={record.Source}; bytes={handle.Length}"),
                    context)
            }, ct);
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }

        if (!audited)
        {
            await handle.DisposeAsync();
            LogRefused(DownloadOperation, fileIndexId, nameof(FileContentOutcome.AuditWriteFailed));
            return Outcome(FileContentOutcome.AuditWriteFailed);
        }

        _logger.LogInformation(
            "File {Operation} served for {FileIndexId} ({Extension}, {SizeBytes} bytes, {ViewMode}) in {ElapsedMs} ms",
            DownloadOperation, fileIndexId, resolution.Extension, handle.Length, "Original", stopwatch.ElapsedMilliseconds);

        // Original bytes, unchanged — no cap, no signature check, no transcoding (§6.3).
        return new FileAccessResult
        {
            Outcome = FileContentOutcome.Success,
            Content = handle.Stream,
            ContentType = DownloadContentTypes[resolution.Extension!],
            FileName = record.FileName
        };
    }

    /// <summary>§6 pipeline steps 1–6. On success the caller owns the returned handle.</summary>
    private async Task<Resolution> ResolveAsync(int fileIndexId, string operation, CancellationToken ct)
    {
        // Step 1 (V3/V4).
        var record = await _fileIndexRepository.GetAsync(fileIndexId, ct);
        if (record is null)
        {
            LogRefused(operation, fileIndexId, ReasonFor(FileContentOutcome.FileNotFound));
            return new Resolution(FileContentOutcome.FileNotFound, null, null, null);
        }

        if (!record.IsActive)
        {
            LogRefused(operation, fileIndexId, ReasonFor(FileContentOutcome.NoLongerAvailable));
            return new Resolution(FileContentOutcome.NoLongerAvailable, record, null, null);
        }

        // Step 2 (V5).
        var extension = record.FileExtension?.Trim().TrimStart('.').ToLowerInvariant();
        if (extension is null || !DownloadContentTypes.ContainsKey(extension))
        {
            LogRefused(operation, fileIndexId, ReasonFor(FileContentOutcome.UnsupportedType));
            return new Resolution(FileContentOutcome.UnsupportedType, record, null, null);
        }

        // Steps 3–6 (V6–V8).
        var handle = await _mirrorFileReader.OpenReadAsync(record, ct);
        var outcome = handle.Outcome switch
        {
            MirrorFileOpenOutcome.Opened => FileContentOutcome.Success,
            MirrorFileOpenOutcome.MappingMissing => FileContentOutcome.MappingMissing,
            MirrorFileOpenOutcome.PathRejected => FileContentOutcome.PathRejected,
            _ => FileContentOutcome.MissingFromMirror
        };

        if (outcome != FileContentOutcome.Success)
        {
            await handle.DisposeAsync();
            if (outcome == FileContentOutcome.PathRejected)
            {
                // V7 security event — fileIndexId only, never a path (§11).
                _logger.LogWarning("Mirror path containment check failed for {FileIndexId}", fileIndexId);
            }

            LogRefused(operation, fileIndexId, ReasonFor(outcome));
            return new Resolution(outcome, record, null, extension);
        }

        return new Resolution(FileContentOutcome.Success, record, handle, extension);
    }

    /// <summary>§8.1 view mode from extension, size cap (§9) and signature (V9). Leaves the
    /// stream positioned at 0.</summary>
    private async Task<(string ViewMode, string? DownloadOnlyReason)> ComputeViewModeAsync(
        string extension, MirrorFileHandle handle, CancellationToken ct)
    {
        if (extension == "txt")
        {
            // Never DownloadOnly — oversize text is truncated, and TXT has no signature check.
            return (FileViewModes.InlineText, null);
        }

        var stream = handle.Stream!;
        var header = new byte[8];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        stream.Position = 0;

        return Classify(extension, handle.Length, header.AsSpan(0, read));
    }

    private (string ViewMode, string? DownloadOnlyReason) Classify(string extension, long length, ReadOnlySpan<byte> head) =>
        extension switch
        {
            "pdf" => length > _options.PdfMaxBytes ? (FileViewModes.DownloadOnly, TooLarge)
                : !head.StartsWith(PdfSignature) ? (FileViewModes.DownloadOnly, SignatureMismatch)
                : (FileViewModes.InlinePdf, null),
            "doc" => OfficeMode(length, head, Ole2Signature, FileViewModes.ExtractedText),
            "xls" => OfficeMode(length, head, Ole2Signature, FileViewModes.HtmlPreview),
            _ => OfficeMode(length, head, ZipSignature, FileViewModes.HtmlPreview) // docx, xlsx
        };

    private (string, string?) OfficeMode(long length, ReadOnlySpan<byte> head, byte[] signature, string viewMode) =>
        length > _options.OfficeMaxBytes ? (FileViewModes.DownloadOnly, TooLarge)
        : !head.StartsWith(signature) ? (FileViewModes.DownloadOnly, SignatureMismatch)
        : (viewMode, null);

    private static async Task<byte[]> ReadUpToAsync(MirrorFileHandle handle, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = await handle.Stream!.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: false, ct);
        return read == count ? buffer : buffer[..read];
    }

    private async Task<FileAccessResult> RefusePreviewAsync(
        int actingUserId, string tabModule, FileIndexRecord record, string reason, string? context, CancellationToken ct)
    {
        LogRefused(ViewOperation, record.FileIndexId, reason);
        return await AuditFailureAsync(
            AuditAction.FileViewed, actingUserId, tabModule, record, record.FileIndexId,
            FileContentOutcome.PreviewNotAvailable, reason, previewReason: reason, context, ct);
    }

    /// <summary>V3–V9 are audited as <c>Failure</c> (§12). If that audit write itself fails, the
    /// response is V10 <c>audit-write-failed</c> — nothing goes out ahead of the audit row.</summary>
    private async Task<FileAccessResult> AuditFailureAsync(
        AuditAction action,
        int actingUserId,
        string tabModule,
        FileIndexRecord? record,
        int fileIndexId,
        FileContentOutcome outcome,
        string reason,
        string? previewReason,
        string? context,
        CancellationToken ct)
    {
        var audited = await TryAuditAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = actingUserId,
            TabModule = tabModule,
            Action = action,
            // V3: FilePath null (no row). Otherwise relative_path — never absolute.
            FilePath = record?.RelativePath,
            TaskQueueId = null,
            Result = AuditResult.Failure,
            ResultDetail = WithContext(
                string.Create(CultureInfo.InvariantCulture, $"fileIndexId={fileIndexId}; reason={reason}"), context)
        }, ct);

        if (!audited)
        {
            return Outcome(FileContentOutcome.AuditWriteFailed);
        }

        return new FileAccessResult { Outcome = outcome, PreviewReason = previewReason };
    }

    private async Task<bool> TryAuditAsync(AuditLogEntry entry, CancellationToken ct)
    {
        try
        {
            await _auditLogService.WriteAsync(entry, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // AuditLogService already logs the failure (CAS-005 §11); the caller maps this to V10.
            return false;
        }
    }

    private FileAccessResult RejectContext(string operation, int fileIndexId)
    {
        // V2 — not audited (tab module unknown). The raw context value is not logged.
        LogRefused(operation, fileIndexId, nameof(FileContentOutcome.ValidationFailed));
        return Outcome(FileContentOutcome.ValidationFailed);
    }

    private void LogRefused(string operation, int fileIndexId, string reason) =>
        _logger.LogWarning("File {Operation} refused for {FileIndexId}: {Reason}", operation, fileIndexId, reason);

    private static bool TryMapContext(string? context, out string tabModule)
    {
        if (context is not null && ContextTabModules.TryGetValue(context, out var mapped))
        {
            tabModule = mapped;
            return true;
        }

        tabModule = string.Empty;
        return false;
    }

    /// <summary>CAS-043 §3 — only <c>context=FileBrowser</c> rows carry the suffix; the other
    /// contexts' <c>result_detail</c> text is unchanged.</summary>
    private static string WithContext(string detail, string? context) =>
        context == FileBrowserContext ? detail + "; context=FileBrowser" : detail;

    private static string SuccessDetail(FileIndexRecord record, string viewMode, long bytes) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"fileIndexId={record.FileIndexId}; source={record.Source}; viewMode={viewMode}; bytes={bytes}");

    /// <summary>§7 audit <c>reason</c> tokens for V3–V8.</summary>
    private static string ReasonFor(FileContentOutcome outcome) => outcome switch
    {
        FileContentOutcome.FileNotFound => "FileIndexNotFound",
        FileContentOutcome.NoLongerAvailable => "NoLongerAvailable",
        FileContentOutcome.UnsupportedType => "UnsupportedType",
        FileContentOutcome.MappingMissing => "MappingMissing",
        FileContentOutcome.PathRejected => "PathRejected",
        FileContentOutcome.MissingFromMirror => "MissingFromMirror",
        _ => outcome.ToString()
    };

    private static FileAccessResult Outcome(FileContentOutcome outcome) => new() { Outcome = outcome };

    private sealed record Resolution(
        FileContentOutcome Outcome,
        FileIndexRecord? Record,
        MirrorFileHandle? Handle,
        string? Extension);
}
