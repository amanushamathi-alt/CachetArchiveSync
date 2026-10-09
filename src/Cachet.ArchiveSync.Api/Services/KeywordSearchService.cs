using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IKeywordSearchService"/> implementation of CAS-006 §6/§6.2 —
/// <c>POST /search</c> validate+submit and <c>GET /search/{taskId}/results</c> read.
/// </summary>
public sealed class KeywordSearchService : IKeywordSearchService
{
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "Keyword Search";

    // CAS-038 A2 V14: bounds B2 cross-check volume for no-term searches (design decision; code constant, not config).
    private const int DateOnlyMaxRangeDays = 31;

    // CAS-047 §9 V2: Filename/Keyword input is 3-200 characters after trim.
    private const int MinTermLengthForTermModes = 3;

    private const string FilenameMode = "Filename";
    private const string KeywordMode = "Keyword";
    private const string DateMode = "Date";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.Ordinal)
    {
        "All", "PDF", "DOC", "DOCX", "XLS", "XLSX", "TXT"
    };

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly ISearchResultRepository _searchResultRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KeywordSearchService> _logger;

    public KeywordSearchService(
        ITaskQueueRepository taskQueueRepository,
        ISearchResultRepository searchResultRepository,
        IAuditLogService auditLogService,
        IConfiguration configuration,
        ILogger<KeywordSearchService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _searchResultRepository = searchResultRepository;
        _auditLogService = auditLogService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SearchSubmitResult> SubmitAsync(SearchRequest request, int submittedByUserId, CancellationToken ct)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return new SearchSubmitResult { Outcome = SearchSubmitOutcome.ValidationFailed, ValidationError = validationError };
        }

        var payload = new KeywordSearchRequestDto
        {
            // CAS-038 A2: a date-only search persists Term = "" (trimmed empty/whitespace term).
            Term = request.Term?.Trim() ?? string.Empty,
            Bucket = request.Bucket,
            Extension = request.Extension,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            Mode = request.Mode
        };
        var requestParameters = JsonSerializer.Serialize(payload);

        var submittedAt = DateTimeOffset.UtcNow;
        var taskId = await _taskQueueRepository.InsertAsync(
            ActingProcessIdentity, ActingProcessIdentity, "KeywordSearch", submittedByUserId, requestParameters, ct);

        // §12/CLAUDE.md: audited before the response, even though the API design doc's §4.1
        // itself carries no explicit [audited] tag (§12's reconciliation note).
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = submittedByUserId,
            TabModule = TabModule,
            Action = AuditAction.KeywordSearchSubmitted,
            TaskQueueId = taskId,
            Result = AuditResult.Success,
            // CAS-047 §11: the mode only, never the term.
            ResultDetail = request.Mode is null ? null : $"Mode: {request.Mode}"
        }, ct);

        _logger.LogInformation("KeywordSearch task {TaskId} submitted by user {UserId}", taskId, submittedByUserId);

        return new SearchSubmitResult
        {
            Outcome = SearchSubmitOutcome.Accepted,
            Response = new SearchSubmittedResponse
            {
                TaskId = taskId,
                Type = "KeywordSearch",
                Status = "Queued",
                SubmittedAt = submittedAt
            }
        };
    }

    public async Task<SearchResultsQueryResult> GetResultsAsync(int taskId, int page, int pageSize, CancellationToken ct)
    {
        var task = await _taskQueueRepository.GetAsync(taskId, ct);

        if (task is null || task.TaskType != "KeywordSearch")
        {
            return new SearchResultsQueryResult { Outcome = SearchResultsOutcome.TaskNotFound };
        }

        if (task.Status is "Queued" or "Running")
        {
            return new SearchResultsQueryResult { Outcome = SearchResultsOutcome.TaskNotComplete };
        }

        // CAS-056 §2.6 — a Cancelled task returns the matches found before the stop, labelled partial.
        if (task.Status is "Completed" or "Cancelled")
        {
            var isCancelled = task.Status == "Cancelled";
            var (filenameItems, filenameTotal) = await _searchResultRepository.ListAsync(taskId, "Filename", page, pageSize, ct);
            var (contentItems, contentTotal) = await _searchResultRepository.ListAsync(taskId, "Content", page, pageSize, ct);

            var response = new SearchResultsResponse
            {
                TaskId = taskId,
                Status = task.Status,
                IsPartial = isCancelled,
                ItemsProcessed = isCancelled ? task.ItemsProcessed : null,
                ItemsTotal = isCancelled ? task.ItemsTotal : null,
                FilenameMatches = new PagedResult<FilenameMatchItem>
                {
                    Items = filenameItems.Select(MapFilenameItem).ToList(),
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = filenameTotal
                },
                ContentMatches = new PagedResult<ContentMatchItem>
                {
                    Items = contentItems.Select(MapContentItem).ToList(),
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = contentTotal
                }
            };

            return new SearchResultsQueryResult { Outcome = SearchResultsOutcome.Found, Response = response };
        }

        // Failed (Completed and Cancelled, CAS-056, are handled above).
        var failureReason = TryGetFailureReason(task.ResultDetail);
        return new SearchResultsQueryResult
        {
            Outcome = SearchResultsOutcome.Found,
            Response = new SearchResultsResponse
            {
                TaskId = taskId,
                Status = "Failed",
                FailureReason = failureReason
            }
        };
    }

    private static string? TryGetFailureReason(string? resultDetail)
    {
        if (string.IsNullOrWhiteSpace(resultDetail))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(resultDetail)?.FailureReason;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FilenameMatchItem MapFilenameItem(SearchResultRow row) => new()
    {
        FileId = row.SearchResultId,
        FileIndexId = row.FileIndexId,
        Name = row.FileName,
        Path = row.RelativePath,
        Size = row.FileSizeBytes,
        ModifiedAt = row.ModifiedAt,
        Status = row.MatchStatus
    };

    private static ContentMatchItem MapContentItem(SearchResultRow row) => new()
    {
        FileId = row.SearchResultId,
        FileIndexId = row.FileIndexId,
        Name = row.FileName,
        Path = row.RelativePath,
        Snippet = row.MatchSnippet ?? string.Empty,
        ModifiedAt = row.ModifiedAt,
        Status = row.MatchStatus
    };

    private string? Validate(SearchRequest request)
    {
        var maxTermLength = _configuration.GetValue<int?>("Search:MaxTermLength") ?? 200;
        var term = request.Term?.Trim() ?? string.Empty;
        var hasBothDates = request.FromDate.HasValue && request.ToDate.HasValue;

        // CAS-047 §9 V1: mode, when present, is exactly Filename, Keyword or Date (case-sensitive).
        if (request.Mode is not null && request.Mode is not (FilenameMode or KeywordMode or DateMode))
        {
            return "'mode' must be one of Filename, Keyword, Date.";
        }

        // CAS-047 §9 V2: Filename/Keyword need a 3-200 character trimmed term and no dates.
        if (request.Mode is FilenameMode or KeywordMode)
        {
            if (term.Length < MinTermLengthForTermModes || term.Length > maxTermLength)
            {
                return $"'term' must be at least {MinTermLengthForTermModes} and at most {maxTermLength} characters.";
            }

            if (request.FromDate.HasValue || request.ToDate.HasValue)
            {
                return "'fromDate' and 'toDate' are not allowed for this mode.";
            }
        }

        // CAS-047 §9 V3: Date mode = blank term, both dates required (V11-V14 apply below).
        if (request.Mode == DateMode)
        {
            if (term.Length != 0)
            {
                return "'term' must be blank for Date mode.";
            }

            if (!hasBothDates)
            {
                return "'fromDate' and 'toDate' are required for Date mode.";
            }
        }

        // CAS-038 A2 V1 (amended): an empty term is allowed only when both dates are supplied
        // (date-only search); a non-empty term is still capped at maxTermLength.
        if ((term.Length == 0 && !hasBothDates) || term.Length > maxTermLength)
        {
            return $"'term' is required and must be at most {maxTermLength} characters.";
        }

        var configuredBucket = _configuration["B2:BucketName"];
        if (string.IsNullOrWhiteSpace(configuredBucket) || !string.Equals(request.Bucket, configuredBucket, StringComparison.Ordinal))
        {
            return "'bucket' must equal the configured B2 bucket.";
        }

        if (string.IsNullOrEmpty(request.Extension) || !AllowedExtensions.Contains(request.Extension))
        {
            return "'extension' must be one of All, PDF, DOC, DOCX, XLS, XLSX, TXT.";
        }

        // CAS-038 §9 V11–V13 — optional created_at range; both null = keyword-only (unchanged).
        if (request.FromDate.HasValue != request.ToDate.HasValue)
        {
            return "'fromDate' and 'toDate' must be supplied together.";
        }

        if (request.FromDate is { } fromDate && request.ToDate is { } toDate)
        {
            if (fromDate > toDate)
            {
                return "'fromDate' must be on or before 'toDate'.";
            }

            var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
            if (fromDate > todayUtc || toDate > todayUtc)
            {
                return "Dates cannot be in the future.";
            }

            // CAS-038 A2 V14: a date-only search (no term) is limited to an inclusive range of
            // DateOnlyMaxRangeDays; keyword + date searches have no range limit.
            if (term.Length == 0 && (toDate.DayNumber - fromDate.DayNumber + 1) > DateOnlyMaxRangeDays)
            {
                return $"Without a keyword, the date range can be at most {DateOnlyMaxRangeDays} days.";
            }
        }

        return null;
    }
}
