using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IPostActionReportService"/> implementation of CAS-010 §3/§6.2 (FR-26; CAS-015 §2/§8).
/// Sources rows from <c>IAuditLogRepository.ListAsync</c> (CAS-009, called directly — not through the
/// <c>GET /audit-log</c> HTTP validation layer, since this is server-side reuse), filtered
/// in-memory to the §6.2 state-changing <c>AuditAction</c> allow-list. §7 V6: if the returned row
/// count equals the internal 100,000-row cap exactly, the result is ambiguous (cannot tell if it is
/// the true total or a truncation) — this fails loud rather than silently returning a partial,
/// incomplete defensible record. The single format requested (<c>request.Format</c>) is rendered
/// entirely in memory and returned directly to the caller — no file is ever written to disk and no
/// <c>dbo.report</c> row is inserted (CAS-015 §2/§9 V8).
/// </summary>
public sealed class PostActionReportService : IPostActionReportService
{
    private const string ReportType = "PostAction";
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "Reports";
    private const int AuditLogReadCap = 100_000;
    private const string PdfFormat = "pdf";
    private const string XlsxFormat = "xlsx";
    private const string PdfContentType = "application/pdf";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // §6.2's allow-list table — every other AuditAction member (Login, CrossCheckExcluded,
    // PreActionReportGenerated/PostActionReportGenerated themselves, etc.) is excluded (§7 V3).
    private static readonly IReadOnlyDictionary<string, string> ActionTakenByAuditAction =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(AuditAction.KeywordSearchSubmitted)] = "Searched",
            [nameof(AuditAction.ContentScanSubmitted)] = "Searched",
            [nameof(AuditAction.QuarantineAdd)] = "Quarantined",
            [nameof(AuditAction.Deleted)] = "Deleted Permanently",
            [nameof(AuditAction.SyncCopyToB2)] = "Copied to B2",
            [nameof(AuditAction.SyncUpdateInB2)] = "Updated in B2",
            [nameof(AuditAction.SyncPurgeFromB2)] = "Purged from B2"
        };

    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IReportDocumentWriter _reportDocumentWriter;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<PostActionReportService> _logger;

    public PostActionReportService(
        IAuditLogRepository auditLogRepository,
        IReportDocumentWriter reportDocumentWriter,
        IAuditLogService auditLogService,
        ILogger<PostActionReportService> logger)
    {
        _auditLogRepository = auditLogRepository;
        _reportDocumentWriter = reportDocumentWriter;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<PostActionReportResult> GenerateAsync(
        ReportGenerationRequest request, int actingUserId, string actingUsername, CancellationToken ct)
    {
        // §9 V7: format must be exactly "pdf" or "xlsx" (case-sensitive) — no silent default.
        if (request.Format is not (PdfFormat or XlsxFormat))
        {
            return new PostActionReportResult
            {
                Outcome = ReportGenerationOutcome.ValidationFailed,
                ValidationError = "format must be exactly 'pdf' or 'xlsx'."
            };
        }

        var (capHit, rows) = await BuildRowsAsync(actingUserId, ct);

        if (capHit)
        {
            return new PostActionReportResult { Outcome = ReportGenerationOutcome.GenerationFailed };
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(request.ActiveTabLabel)
            ? "Post-Action Outcome Report"
            : $"Post-Action Outcome Report — {request.ActiveTabLabel}";

        var document = new ReportDocument
        {
            Title = title,
            ColumnHeaders = new[] { "File Name", "File Path", "Action Taken", "Source Tab", "Timestamp", "Result" },
            Rows = rows
                .Select(row => (IReadOnlyList<string>)new[]
                {
                    row.FileName ?? string.Empty,
                    row.FilePath ?? string.Empty,
                    row.ActionTaken,
                    row.SourceTab,
                    row.Timestamp.ToString("u"),
                    row.Result
                })
                .ToList(),
            GeneratedAt = generatedAt,
            GeneratedBy = actingUsername
        };

        var isPdf = request.Format == PdfFormat;
        var downloadFileName = $"post-action-report-{generatedAt:yyyyMMddHHmmss}.{request.Format}";

        byte[] fileBytes;
        try
        {
            fileBytes = isPdf
                ? await _reportDocumentWriter.WritePdfAsync(document, ct)
                : await _reportDocumentWriter.WriteXlsxAsync(document, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // §7 V2: PdfSharp/ClosedXML failures never bubble as an unhandled 500.
            _logger.LogError(ex,
                "Report generation failed for {ReportType} requested by {UserId}", ReportType, actingUserId);
            return new PostActionReportResult { Outcome = ReportGenerationOutcome.GenerationFailed };
        }

        // §11/§12: audit row written before the response returns (SC-5). Nothing is persisted to
        // dbo.report or disk — only the fact-of-generation is recorded (CAS-015 §2).
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = actingUserId,
            TabModule = TabModule,
            Action = AuditAction.PostActionReportGenerated,
            FilePath = null,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation(
            "{Action} completed for {ReportType} requested by {UserId}",
            AuditAction.PostActionReportGenerated, ReportType, actingUserId);

        return new PostActionReportResult
        {
            Outcome = ReportGenerationOutcome.Generated,
            FileBytes = fileBytes,
            ContentType = isPdf ? PdfContentType : XlsxContentType,
            DownloadFileName = downloadFileName
        };
    }

    /// <summary>CAS-038 §3/§6 — <c>GET /reports/post-action/preview</c>. Builds rows via the exact
    /// same <see cref="BuildRowsAsync"/> call <see cref="GenerateAsync"/> uses (§3 V2/SC-3),
    /// including its 100,000-row cap fail-loud rule (§7 V5, same as §7 V6 for the downloaded file),
    /// and returns them as JSON. No audit row is written (§12) and no PDF/XLSX is rendered.</summary>
    public async Task<PostActionReportPreviewResult> PreviewAsync(
        int actingUserId, string actingUsername, CancellationToken ct)
    {
        var (capHit, rows) = await BuildRowsAsync(actingUserId, ct);

        if (capHit)
        {
            return new PostActionReportPreviewResult { Outcome = ReportPreviewOutcome.GenerationFailed };
        }

        var generatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "ReportPreview: {ReportType} preview returned {RowCount} rows for {UserId}",
            ReportType, rows.Count, actingUserId);

        return new PostActionReportPreviewResult
        {
            Outcome = ReportPreviewOutcome.Generated,
            Response = new PostActionReportPreview
            {
                GeneratedAt = generatedAt,
                GeneratedBy = actingUsername,
                Rows = rows
            }
        };
    }

    /// <summary>Shared row-aggregation step (CAS-038 §3 V2/SC-3) — reads
    /// <c>IAuditLogRepository.ListAsync</c> (CAS-009), filtered to the §6.2 state-changing
    /// <c>AuditAction</c> allow-list. §7 V6/CAS-038 §7 V5: if the read hits the internal
    /// <see cref="AuditLogReadCap"/>-row cap exactly, the result is ambiguous (cannot tell a true
    /// total from a truncation) — returns <c>CapHit: true</c> with an empty row list rather than
    /// silently returning a partial, indefensible record. Used by both <see cref="GenerateAsync"/>
    /// (the downloaded file) and <see cref="PreviewAsync"/> (the on-screen preview) so the two code
    /// paths can never drift.</summary>
    private async Task<(bool CapHit, List<PostActionReportRow> Rows)> BuildRowsAsync(
        int actingUserId, CancellationToken ct)
    {
        var (auditRows, _) = await _auditLogRepository.ListAsync(
            null, null, null, 1, AuditLogReadCap, ct);

        if (auditRows.Count == AuditLogReadCap)
        {
            // §7 V6: ambiguous — cannot distinguish "exactly the cap" from "truncated at the cap".
            _logger.LogError(
                "Report generation failed for {ReportType} requested by {UserId}: audit-log read hit the {Cap}-row cap exactly",
                ReportType, actingUserId, AuditLogReadCap);
            return (true, new List<PostActionReportRow>());
        }

        var rows = auditRows
            .Where(row => ActionTakenByAuditAction.ContainsKey(row.Action))
            .Select(row => new PostActionReportRow
            {
                FileName = row.FilePath is null ? null : Path.GetFileName(row.FilePath),
                FilePath = row.FilePath,
                ActionTaken = ActionTakenByAuditAction[row.Action],
                SourceTab = row.TabModule,
                Timestamp = row.CreatedAt,
                Result = row.Result
            })
            .ToList();

        return (false, rows);
    }
}
