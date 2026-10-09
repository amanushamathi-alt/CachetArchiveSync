using System.Diagnostics;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Helpers;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ISearchResultBrowseService"/> implementation of CAS-049 §8/§9. Reads
/// <c>dbo.search_result</c> joined to <c>dbo.file_index</c> metadata only. Ownership is the
/// claim-derived <c>userId</c> (V1); the bucket comes from <c>B2:BucketName</c> and the prefix from the
/// mapping table, never from the client, and the prefix never appears in a response (§8). Folder
/// validation and path composition follow <see cref="FileBrowseService"/> (CAS-043). Never logs the
/// folder value or search terms (§11). Reads are not audited (§11).
/// </summary>
public sealed class SearchResultBrowseService : ISearchResultBrowseService
{
    private const string KeywordSearchTaskType = "KeywordSearch";
    private const string CompletedStatus = "Completed";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly ISearchResultBrowseRepository _repository;
    private readonly IB2PathPrefixProvider _prefixProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SearchResultBrowseService> _logger;

    public SearchResultBrowseService(
        ITaskQueueRepository taskQueueRepository,
        ISearchResultBrowseRepository repository,
        IB2PathPrefixProvider prefixProvider,
        IConfiguration configuration,
        ILogger<SearchResultBrowseService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _repository = repository;
        _prefixProvider = prefixProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SearchResultBrowseResult> BrowseSearchResultsAsync(
        int taskId, int userId, string? folder, bool foldersOnly, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        folder ??= string.Empty;

        if (!IsValidFolder(folder))
        {
            _logger.LogWarning("Search results browse rejected folder parameter for {UserId}", userId);
            return BrowseOutcome(SearchResultBrowseOutcome.ValidationFailed);
        }

        try
        {
            var context = await ResolveAsync(taskId, userId, folder, ct);
            if (context.Failure is { } failure)
            {
                return BrowseOutcome(failure);
            }

            var bucketName = context.BucketName!;
            var prefix = context.Prefix!;
            var fullPath = context.FullPath!;

            var rows = await _repository.BrowseAsync(taskId, bucketName, fullPath, foldersOnly, ct);

            // V4 — a non-empty folder holding no match of this task. With foldersOnly the SP emits no
            // files, so an empty folder list could be a leaf folder; re-query with files to tell apart.
            if (folder.Length > 0 && rows.Folders.Count == 0)
            {
                var probe = foldersOnly
                    ? await _repository.BrowseAsync(taskId, bucketName, fullPath, false, ct)
                    : rows;
                if (probe.Folders.Count == 0 && probe.Files.Count == 0)
                {
                    _logger.LogWarning("Search results browse failed for {UserId}: {Reason}", userId, "FolderNotFound");
                    return BrowseOutcome(SearchResultBrowseOutcome.FolderNotFound);
                }
            }

            var segments = folder.Length == 0 ? Array.Empty<string>() : folder.Split('/');
            var folderPrefix = folder.Length == 0 ? string.Empty : folder + "/";

            var folders = rows.Folders
                .Select(row => new BrowseFolderItem
                {
                    Name = row.FolderName,
                    Path = folderPrefix + row.FolderName,
                    LatestModifiedAt = row.LatestModifiedAt,
                    HasSubfolders = row.HasSubfolders
                })
                .ToList();

            var files = rows.Files
                .Select(row => new BrowseFileItem
                {
                    FileIndexId = row.FileIndexId,
                    Name = row.FileName,
                    Extension = row.FileExtension?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty,
                    SizeBytes = row.FileSizeBytes,
                    ModifiedAt = row.ModifiedAt,
                    SearchResultId = row.SearchResultId,
                    MatchStatus = row.MatchStatus
                })
                .ToList();

            var breadcrumb = new List<BreadcrumbItem>(segments.Length);
            for (var i = 0; i < segments.Length; i++)
            {
                breadcrumb.Add(new BreadcrumbItem
                {
                    Name = segments[i],
                    Path = string.Join('/', segments, 0, i + 1)
                });
            }

            var response = new FolderBrowseResponse
            {
                RootName = prefix.Length == 0 ? bucketName : prefix[(prefix.LastIndexOf('/') + 1)..],
                Folder = folder,
                Parent = segments.Length == 0 ? null : string.Join('/', segments, 0, segments.Length - 1),
                Breadcrumb = breadcrumb,
                Folders = folders,
                Files = files,
                TotalFolders = folders.Count,
                TotalFiles = files.Count
            };

            _logger.LogInformation(
                "Search results browse for {UserId} task {TaskId}: {FolderDepth} deep, {FolderCount} folders, {FileCount} files, {ElapsedMs} ms",
                userId, taskId, segments.Length, folders.Count, files.Count, stopwatch.ElapsedMilliseconds);

            return new SearchResultBrowseResult { Outcome = SearchResultBrowseOutcome.Success, Response = response };
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // A client abort is not a browse failure; SqlClient may surface it as SqlException.
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            // V6 — exception type only, never its message (may carry SQL or parameter text) or the folder.
            _logger.LogWarning(
                "Search results browse failed for {UserId}: {Reason} ({ExceptionType})",
                userId, "BrowseFailed", ex.GetType().Name);
            return BrowseOutcome(SearchResultBrowseOutcome.BrowseFailed);
        }
    }

    public async Task<SearchResultIdsResult> GetResultIdsByFolderAsync(
        int taskId, int userId, string? folder, CancellationToken ct)
    {
        folder ??= string.Empty;

        if (!IsValidFolder(folder))
        {
            _logger.LogWarning("Search result ids rejected folder parameter for {UserId}", userId);
            return IdsOutcome(SearchResultBrowseOutcome.ValidationFailed);
        }

        try
        {
            var context = await ResolveAsync(taskId, userId, folder, ct);
            if (context.Failure is { } failure)
            {
                return IdsOutcome(failure);
            }

            // Ask for one more than the cap to detect overflow (V8).
            var ids = await _repository.ListIdsByFolderAsync(
                taskId, context.BucketName!, context.FullPath!, ISearchResultBrowseService.ResultIdsLimit + 1, ct);

            if (ids.Count > ISearchResultBrowseService.ResultIdsLimit)
            {
                _logger.LogWarning("Search result ids failed for {UserId}: {Reason}", userId, "SelectionTooLarge");
                return IdsOutcome(SearchResultBrowseOutcome.SelectionTooLarge);
            }

            // V4 (via V7) — a non-empty folder holding no match of this task.
            if (folder.Length > 0 && ids.Count == 0)
            {
                _logger.LogWarning("Search result ids failed for {UserId}: {Reason}", userId, "FolderNotFound");
                return IdsOutcome(SearchResultBrowseOutcome.FolderNotFound);
            }

            return new SearchResultIdsResult
            {
                Outcome = SearchResultBrowseOutcome.Success,
                Response = new SearchResultIdsResponse { SearchResultIds = ids, Count = ids.Count }
            };
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Search result ids failed for {UserId}: {Reason} ({ExceptionType})",
                userId, "BrowseFailed", ex.GetType().Name);
            return IdsOutcome(SearchResultBrowseOutcome.BrowseFailed);
        }
    }

    /// <summary>V1 ownership/type/completion, then bucket + prefix + full key path. Throws on DB failure
    /// (callers map to <c>BrowseFailed</c>).</summary>
    private async Task<BrowseContext> ResolveAsync(int taskId, int userId, string folder, CancellationToken ct)
    {
        var task = await _taskQueueRepository.GetAsync(taskId, ct);
        if (task is null || task.TaskType != KeywordSearchTaskType || task.SubmittedByUserId != userId)
        {
            return BrowseContext.Fail(SearchResultBrowseOutcome.TaskNotFound);
        }

        // CAS-056 §2.6 — a Cancelled task's partial results are browsable like a Completed one's.
        if (task.Status is not (CompletedStatus or "Cancelled"))
        {
            return BrowseContext.Fail(SearchResultBrowseOutcome.TaskNotComplete);
        }

        var bucketName = _configuration["B2:BucketName"];
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            _logger.LogWarning("Search results browse failed for {UserId}: {Reason}", userId, "BucketNotConfigured");
            return BrowseContext.Fail(SearchResultBrowseOutcome.BucketNotConfigured);
        }

        var prefix = await _prefixProvider.GetPrefixAsync(bucketName, ct);
        if (prefix is null)
        {
            _logger.LogWarning("Search results browse failed for {UserId}: {Reason}", userId, "MappingMissing");
            return BrowseContext.Fail(SearchResultBrowseOutcome.MappingMissing);
        }

        var fullPath = B2KeyPath.Combine(prefix, folder);

        // The SP's @folder_path is NVARCHAR(1000); never let it truncate silently.
        if (fullPath.Length > FileBrowseService.MaxFolderLength)
        {
            return BrowseContext.Fail(SearchResultBrowseOutcome.ValidationFailed);
        }

        return new BrowseContext(null, bucketName, prefix, fullPath);
    }

    /// <summary>V2 — CAS-043 V1/V2 rules, reused from <see cref="FileBrowseService"/>.</summary>
    private static bool IsValidFolder(string folder) =>
        folder.Length <= FileBrowseService.MaxFolderLength && FileBrowseService.IsWellFormedFolder(folder);

    private static SearchResultBrowseResult BrowseOutcome(SearchResultBrowseOutcome outcome) => new() { Outcome = outcome };

    private static SearchResultIdsResult IdsOutcome(SearchResultBrowseOutcome outcome) => new() { Outcome = outcome };

    private sealed record BrowseContext(
        SearchResultBrowseOutcome? Failure, string? BucketName, string? Prefix, string? FullPath)
    {
        public static BrowseContext Fail(SearchResultBrowseOutcome outcome) => new(outcome, null, null, null);
    }
}
