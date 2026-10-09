using System.Diagnostics;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Helpers;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IFileBrowseService"/> implementation of CAS-043 §6/§7. Reads <c>dbo.file_index</c>
/// metadata only — no file system, B2 or file content. Source is fixed to <c>B2</c> and the bucket
/// comes from <c>B2:BucketName</c>; neither is a client value. The bucket prefix is prepended to
/// the client's root-relative folder for the SP call and never appears in the response, because
/// every returned path is built from the client folder plus a single child segment. Never logs the
/// folder value or file names (§11). Browsing is not audited (§12).
/// </summary>
public sealed class FileBrowseService : IFileBrowseService
{
    private const string BrowseSource = "B2";
    internal const int MaxFolderLength = 1000;

    private readonly IFileBrowseRepository _repository;
    private readonly IB2PathPrefixProvider _prefixProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileBrowseService> _logger;

    public FileBrowseService(
        IFileBrowseRepository repository,
        IB2PathPrefixProvider prefixProvider,
        IConfiguration configuration,
        ILogger<FileBrowseService> logger)
    {
        _repository = repository;
        _prefixProvider = prefixProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<FileBrowseResult> BrowseAsync(
        string? folder, bool foldersOnly, int userId, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        folder ??= string.Empty;

        // V1–V2 (V4 is parsed by the controller).
        if (folder.Length > MaxFolderLength)
        {
            return Reject("V1", userId);
        }

        if (!IsWellFormedFolder(folder))
        {
            return Reject("V2", userId);
        }

        // V6 — bucket configured (never a secret) and an active mapping row.
        var bucketName = _configuration["B2:BucketName"];
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            _logger.LogWarning("Files browse failed for {UserId}: {Reason}", userId, "BucketNotConfigured");
            return Outcome(FileBrowseOutcome.BucketNotConfigured);
        }

        try
        {
            var prefix = await _prefixProvider.GetPrefixAsync(bucketName, ct);
            if (prefix is null)
            {
                _logger.LogWarning("Files browse failed for {UserId}: {Reason}", userId, "MappingMissing");
                return Outcome(FileBrowseOutcome.MappingMissing);
            }

            var fullPath = B2KeyPath.Combine(prefix, folder);

            // The SP's @folder_path is NVARCHAR(1000); never let it truncate silently.
            if (fullPath.Length > MaxFolderLength)
            {
                return Reject("V1", userId);
            }

            var rows = await _repository.BrowseAsync(BrowseSource, bucketName, fullPath, foldersOnly, ct);

            // V5 — a non-empty folder with no active rows beneath it. With foldersOnly the SP emits
            // no files, so an empty folder list could be a leaf folder (files, no subfolders) or an
            // unknown one; re-query with files to tell them apart (SP signature unchanged).
            if (folder.Length > 0 && rows.Folders.Count == 0)
            {
                var probe = foldersOnly
                    ? await _repository.BrowseAsync(BrowseSource, bucketName, fullPath, false, ct)
                    : rows;
                if (probe.Folders.Count == 0 && probe.Files.Count == 0)
                {
                    _logger.LogWarning("Files browse failed for {UserId}: {Reason}", userId, "FolderNotFound");
                    return Outcome(FileBrowseOutcome.FolderNotFound);
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
                    ModifiedAt = row.ModifiedAt
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
                "Files browse for {UserId}: {FolderDepth} deep, {FolderCount} folders, {FileCount} files, {ElapsedMs} ms",
                userId, segments.Length, folders.Count, files.Count, stopwatch.ElapsedMilliseconds);

            return new FileBrowseResult { Outcome = FileBrowseOutcome.Success, Response = response };
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // SqlClient may surface an aborted command as SqlException rather than
            // OperationCanceledException; a client abort is not a browse failure (SC-14).
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            // V9 — generic failure; the exception type only, never its message (may carry SQL,
            // path or parameter text) and never the folder value.
            _logger.LogWarning(
                "Files browse failed for {UserId}: {Reason} ({ExceptionType})",
                userId, "BrowseFailed", ex.GetType().Name);
            return Outcome(FileBrowseOutcome.BrowseFailed);
        }
    }

    /// <summary>V2 — no leading/trailing <c>/</c>, no <c>//</c>, no <c>\</c>, no control characters,
    /// no <c>.</c> or <c>..</c> segment. The empty folder (root) is valid.</summary>
    internal static bool IsWellFormedFolder(string folder)
    {
        if (folder.Length == 0)
        {
            return true;
        }

        foreach (var ch in folder)
        {
            if (ch == '\\' || char.IsControl(ch))
            {
                return false;
            }
        }

        foreach (var segment in folder.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private FileBrowseResult Reject(string ruleId, int userId)
    {
        _logger.LogWarning("Files browse rejected folder parameter for {UserId}: {Rule}", userId, ruleId);
        return Outcome(FileBrowseOutcome.ValidationFailed);
    }

    private static FileBrowseResult Outcome(FileBrowseOutcome outcome) => new() { Outcome = outcome };
}
