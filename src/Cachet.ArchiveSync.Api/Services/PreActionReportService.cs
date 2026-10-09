using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IPreActionReportService"/> implementation of CAS-010 §3/§6.1 (FR-25; CAS-015 §2/§8).
/// Aggregates every active <c>dbo.search_result</c> row (all tasks, via <c>ListAllActiveAsync</c>)
/// and every <c>Active</c> <c>dbo.quarantine_entry</c> row (via <c>ListActiveAsync</c>, CAS-008
/// unchanged) into one report — two rows for a file that is both an active match and actively
/// quarantined (§3's resolved dedup decision, no collapse). The single format requested
/// (<c>request.Format</c>) is rendered entirely in memory and returned directly to the caller — no
/// file is ever written to disk and no <c>dbo.report</c> row is inserted (CAS-015 §2/§9 V8).
/// </summary>
public sealed class PreActionReportService : IPreActionReportService
{
    private const string ReportType = "PreAction";
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "Reports";
    private const string PdfFormat = "pdf";
    private const string XlsxFormat = "xlsx";
    private const string PdfContentType = "application/pdf";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // CK on dbo.search_result.match_type (CAS-006 §3) — 'Filename'/'Content' are the only two
    // values; §3's panel mapping table.
    private static readonly IReadOnlyDictionary<string, string> PanelByMatchType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Filename"] = "Filename Matches",
            ["Content"] = "Content Matches"
        };

    private readonly ISearchResultRepository _searchResultRepository;
    private readonly IQuarantineEntryRepository _quarantineEntryRepository;
    private readonly IReportDocumentWriter _reportDocumentWriter;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<PreActionReportService> _logger;

    public PreActionReportService(
        ISearchResultRepository searchResultRepository,
        IQuarantineEntryRepository quarantineEntryRepository,
        IReportDocumentWriter reportDocumentWriter,
        IAuditLogService auditLogService,
        ILogger<PreActionReportService> logger)
    {
        _searchResultRepository = searchResultRepository;
        _quarantineEntryRepository = quarantineEntryRepository;
        _reportDocumentWriter = reportDocumentWriter;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<PreActionReportResult> GenerateAsync(
        ReportGenerationRequest request, int actingUserId, string actingUsername, CancellationToken ct)
    {
        // §9 V7: format must be exactly "pdf" or "xlsx" (case-sensitive) — no silent default.
        if (request.Format is not (PdfFormat or XlsxFormat))
        {
            return new PreActionReportResult
            {
                Outcome = ReportGenerationOutcome.ValidationFailed,
                ValidationError = "format must be exactly 'pdf' or 'xlsx'."
            };
        }

        var rows = await BuildRowsAsync(ct);

        var generatedAt = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(request.ActiveTabLabel)
            ? "Pre-Action Impact Report"
            : $"Pre-Action Impact Report — {request.ActiveTabLabel}";

        var document = new ReportDocument
        {
            Title = title,
            ColumnHeaders = new[] { "File Name", "File Path", "Panel", "Status" },
            Rows = rows
                .Select(row => (IReadOnlyList<string>)new[] { row.FileName, row.FilePath, row.Panel, row.Status })
                .ToList(),
            GeneratedAt = generatedAt,
            GeneratedBy = actingUsername
        };

        var isPdf = request.Format == PdfFormat;
        var downloadFileName = $"pre-action-report-{generatedAt:yyyyMMddHHmmss}.{request.Format}";

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
            return new PreActionReportResult { Outcome = ReportGenerationOutcome.GenerationFailed };
        }

        // §11/§12: audit row written before the response returns (SC-5). Nothing is persisted to
        // dbo.report or disk — only the fact-of-generation is recorded (CAS-015 §2).
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = actingUserId,
            TabModule = TabModule,
            Action = AuditAction.PreActionReportGenerated,
            FilePath = null,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation(
            "{Action} completed for {ReportType} requested by {UserId}",
            AuditAction.PreActionReportGenerated, ReportType, actingUserId);

        return new PreActionReportResult
        {
            Outcome = ReportGenerationOutcome.Generated,
            FileBytes = fileBytes,
            ContentType = isPdf ? PdfContentType : XlsxContentType,
            DownloadFileName = downloadFileName
        };
    }

    /// <summary>CAS-038 §3/§6 — <c>GET /reports/pre-action/preview</c>. Builds rows via the exact
    /// same <see cref="BuildRowsAsync"/> call <see cref="GenerateAsync"/> uses (§3 V2/SC-3) and
    /// returns them as JSON. No audit row is written (§12) and no PDF/XLSX is rendered.</summary>
    public async Task<PreActionReportPreview> PreviewAsync(
        int actingUserId, string actingUsername, CancellationToken ct)
    {
        var rows = await BuildRowsAsync(ct);
        var generatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "ReportPreview: {ReportType} preview returned {RowCount} rows for {UserId}",
            ReportType, rows.Count, actingUserId);

        return new PreActionReportPreview
        {
            GeneratedAt = generatedAt,
            GeneratedBy = actingUsername,
            Rows = rows
        };
    }

    /// <summary>Shared row-aggregation step (CAS-038 §3 V2/SC-3) — every active
    /// <c>dbo.search_result</c> row (all tasks) mapped to its panel, plus every <c>Active</c>
    /// <c>dbo.quarantine_entry</c> row, no dedup. Used by both <see cref="GenerateAsync"/> (the
    /// downloaded file) and <see cref="PreviewAsync"/> (the on-screen preview) so the two code paths
    /// can never drift.</summary>
    private async Task<List<PreActionReportRow>> BuildRowsAsync(CancellationToken ct)
    {
        var searchRows = await _searchResultRepository.ListAllActiveAsync(ct);
        var quarantineRows = await _quarantineEntryRepository.ListActiveAsync(ct);

        var rows = new List<PreActionReportRow>();

        foreach (var searchRow in searchRows)
        {
            if (!PanelByMatchType.TryGetValue(searchRow.MatchType, out var panel))
            {
                // Defensive: CK_search_result_match_type (CAS-006 §3) already restricts match_type
                // to 'Filename'/'Content'; an unrecognized value here would indicate a schema drift
                // this spec did not anticipate, not a normal runtime case — skip rather than throw.
                continue;
            }

            rows.Add(new PreActionReportRow
            {
                FileName = searchRow.FileName,
                FilePath = searchRow.RelativePath,
                Panel = panel,
                Status = searchRow.MatchStatus
            });
        }

        foreach (var quarantineRow in quarantineRows)
        {
            rows.Add(new PreActionReportRow
            {
                FileName = quarantineRow.Name,
                FilePath = quarantineRow.Path,
                Panel = "Quarantine",
                Status = quarantineRow.Status
            });
        }

        return rows;
    }
}
