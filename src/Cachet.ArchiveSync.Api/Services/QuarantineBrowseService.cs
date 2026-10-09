using System.Globalization;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Helpers;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IQuarantineBrowseService"/> implementation of CAS-050 §2/§8 — Files-browser Quarantine
/// add/remove/state (FR-09a). One <c>quarantine_entry</c> row per file always; a folder action is one
/// set-based stored procedure call. One audit row per add/remove action (not per file), written after
/// the mutation and before the success is returned — the same ordering as <see cref="QuarantineService"/>
/// (FR-23). Only counts, ids and paths are logged or audited — never file content or credentials.
/// </summary>
public sealed class QuarantineBrowseService : IQuarantineBrowseService
{
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "Quarantine";

    private const string ScopeFile = "File";
    private const string ScopeFolder = "Folder";

    private readonly IQuarantineEntryRepository _quarantineEntryRepository;
    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly IB2PathPrefixProvider _prefixProvider;
    private readonly ILogger<QuarantineBrowseService> _logger;

    public QuarantineBrowseService(
        IQuarantineEntryRepository quarantineEntryRepository,
        IFileIndexRepository fileIndexRepository,
        IAuditLogService auditLogService,
        IB2PathPrefixProvider prefixProvider,
        ILogger<QuarantineBrowseService> logger)
    {
        _quarantineEntryRepository = quarantineEntryRepository;
        _fileIndexRepository = fileIndexRepository;
        _auditLogService = auditLogService;
        _prefixProvider = prefixProvider;
        _logger = logger;
    }

    public async Task<QuarantineBrowseAddResult> AddAsync(QuarantineBrowseRequest request, int actingUserId, CancellationToken ct)
    {
        if (!IsValid(request))
        {
            return new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.ValidationFailed };
        }

        // Same actor-identity convention as QuarantineService (numeric user id as quarantine_entry.created_by).
        var actingUserIdentity = actingUserId.ToString(CultureInfo.InvariantCulture);

        if (request.Scope == ScopeFile)
        {
            var fileIndexId = request.FileIndexId!.Value;

            // §9 V3: must be an active file_index row. Also resolves the path for the audit row.
            var file = await _fileIndexRepository.GetAsync(fileIndexId, ct);
            if (file is null || !file.IsActive)
            {
                return new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.FileNotFound };
            }

            var insert = await _quarantineEntryRepository.InsertBrowseFileAsync(
                actingUserIdentity, actingUserIdentity, fileIndexId, ct);

            switch (insert.Outcome)
            {
                case QuarantineInsertOutcome.AlreadyActive:
                    // §9 V4: File scope conflict.
                    return new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.AlreadyQuarantined };
                case QuarantineInsertOutcome.SearchResultNotFound:
                    // The SP's "file_index_id not found or not active" (row deactivated since the lookup).
                    return new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.FileNotFound };
            }

            await WriteAuditAsync(
                actingUserId, AuditAction.QuarantineAdd, file.RelativePath,
                "scope=File; added=1; alreadyQuarantined=0", ct);

            _logger.LogInformation(
                "Quarantine browse add by user {UserId}: scope File, file {FileIndexId}", actingUserId, fileIndexId);

            return new QuarantineBrowseAddResult
            {
                Outcome = QuarantineBrowseAddOutcome.Completed,
                Response = new QuarantineBrowseAddResponse { Added = 1, AlreadyQuarantined = 0 }
            };
        }

        var (pathStatus, fullPath) = await ResolveFullPathAsync(request.Source!, request.BucketName!, request.FolderPath!, ct);
        if (pathStatus == PathStatus.MappingMissing)
        {
            return new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.MappingMissing };
        }

        if (pathStatus == PathStatus.TooLong)
        {
            return new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.ValidationFailed };
        }

        var counts = await _quarantineEntryRepository.InsertBrowseFolderAsync(
            actingUserIdentity, actingUserIdentity, request.Source!, request.BucketName!, fullPath!, ct);
        if (counts is null)
        {
            return new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.FolderNotFound };
        }

        await WriteAuditAsync(
            actingUserId, AuditAction.QuarantineAdd, fullPath,
            string.Create(CultureInfo.InvariantCulture,
                $"scope=Folder; added={counts.Added}; alreadyQuarantined={counts.AlreadyQuarantined}"),
            ct);

        _logger.LogInformation(
            "Quarantine browse add by user {UserId}: scope Folder, {Added} added, {AlreadyQuarantined} already quarantined",
            actingUserId, counts.Added, counts.AlreadyQuarantined);

        return new QuarantineBrowseAddResult
        {
            Outcome = QuarantineBrowseAddOutcome.Completed,
            Response = new QuarantineBrowseAddResponse
            {
                Added = counts.Added,
                AlreadyQuarantined = counts.AlreadyQuarantined
            }
        };
    }

    public async Task<QuarantineBrowseRemoveResult> RemoveAsync(QuarantineBrowseRequest request, int actingUserId, CancellationToken ct)
    {
        if (!IsValid(request))
        {
            return new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.ValidationFailed };
        }

        string? auditPath;
        int removed;

        if (request.Scope == ScopeFile)
        {
            var fileIndexId = request.FileIndexId!.Value;

            removed = await _quarantineEntryRepository.HardDeleteBrowseFileAsync(fileIndexId, ct);
            if (removed == 0)
            {
                return new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.NotQuarantined };
            }

            // Path is audit metadata only; a missing row cannot happen here (the entry joined to it) but is tolerated.
            var file = await _fileIndexRepository.GetAsync(fileIndexId, ct);
            auditPath = file?.RelativePath;
        }
        else
        {
            var (pathStatus, fullPath) = await ResolveFullPathAsync(request.Source!, request.BucketName!, request.FolderPath!, ct);
            if (pathStatus == PathStatus.MappingMissing)
            {
                return new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.MappingMissing };
            }

            if (pathStatus == PathStatus.TooLong)
            {
                return new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.ValidationFailed };
            }

            var deleted = await _quarantineEntryRepository.HardDeleteBrowseFolderAsync(
                request.Source!, request.BucketName!, fullPath!, ct);
            if (deleted is null)
            {
                return new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.FolderNotFound };
            }

            removed = deleted.Value;
            auditPath = fullPath;
        }

        await WriteAuditAsync(
            actingUserId, AuditAction.QuarantineRemoved, auditPath,
            string.Create(CultureInfo.InvariantCulture, $"scope={request.Scope}; removed={removed}"), ct);

        _logger.LogInformation(
            "Quarantine browse remove by user {UserId}: scope {Scope}, {Removed} removed",
            actingUserId, request.Scope, removed);

        return new QuarantineBrowseRemoveResult
        {
            Outcome = QuarantineBrowseRemoveOutcome.Completed,
            Response = new QuarantineBrowseRemoveResponse { Removed = removed }
        };
    }

    public async Task<QuarantineBrowseStateResult> GetStateAsync(
        string? source, string? bucketName, string? folderPath, CancellationToken ct)
    {
        var path = folderPath ?? string.Empty;

        // Same shape rules as the write routes, except an empty folderPath is the bucket root here.
        if (!IsValidSource(source)
            || string.IsNullOrWhiteSpace(bucketName)
            || path.StartsWith('/')
            || path.EndsWith('/'))
        {
            return new QuarantineBrowseStateResult { Outcome = QuarantineBrowseStateOutcome.ValidationFailed };
        }

        var (pathStatus, fullPath) = await ResolveFullPathAsync(source!, bucketName!, path, ct);
        if (pathStatus == PathStatus.MappingMissing)
        {
            return new QuarantineBrowseStateResult { Outcome = QuarantineBrowseStateOutcome.MappingMissing };
        }

        if (pathStatus == PathStatus.TooLong)
        {
            return new QuarantineBrowseStateResult { Outcome = QuarantineBrowseStateOutcome.ValidationFailed };
        }

        var response = await _quarantineEntryRepository.GetBrowseStateAsync(source!, bucketName!, fullPath!, ct);

        return new QuarantineBrowseStateResult
        {
            Outcome = QuarantineBrowseStateOutcome.Completed,
            Response = response
        };
    }

    private enum PathStatus
    {
        Ok,
        MappingMissing,
        TooLong
    }

    /// <summary>
    /// CAS-053 §2 — converts the root-relative <paramref name="folderPath"/> to the full B2 key path
    /// (<c>file_index.relative_path</c>) using the request bucket's <c>b2_path_prefix</c>. B2 only; any
    /// other source passes through unchanged. A missing mapping or a lookup failure never falls back to an
    /// unprefixed path (fail closed: a provider exception propagates as a 500).
    /// </summary>
    private async Task<(PathStatus Status, string? FullPath)> ResolveFullPathAsync(
        string source, string bucketName, string folderPath, CancellationToken ct)
    {
        if (!string.Equals(source, "B2", StringComparison.Ordinal))
        {
            return (PathStatus.Ok, folderPath);
        }

        var prefix = await _prefixProvider.GetPrefixAsync(bucketName, ct);
        if (prefix is null)
        {
            _logger.LogWarning("Quarantine browse failed: {Reason}", "MappingMissing");
            return (PathStatus.MappingMissing, null);
        }

        var fullPath = B2KeyPath.Combine(prefix, folderPath);

        // The SPs' @folder_path is NVARCHAR(1000); never let it truncate silently.
        if (fullPath.Length > FileBrowseService.MaxFolderLength)
        {
            return (PathStatus.TooLong, null);
        }

        return (PathStatus.Ok, fullPath);
    }

    /// <summary>§9 V1 — request shape.</summary>
    private static bool IsValid(QuarantineBrowseRequest request)
    {
        switch (request.Scope)
        {
            case ScopeFile:
                return request.FileIndexId is not null;
            case ScopeFolder:
                return IsValidSource(request.Source)
                    && !string.IsNullOrWhiteSpace(request.BucketName)
                    && !string.IsNullOrWhiteSpace(request.FolderPath)
                    && !request.FolderPath.StartsWith('/')
                    && !request.FolderPath.EndsWith('/');
            default:
                return false;
        }
    }

    private static bool IsValidSource(string? source) =>
        string.Equals(source, "ODrive", StringComparison.Ordinal)
        || string.Equals(source, "B2", StringComparison.Ordinal);

    private Task WriteAuditAsync(int actingUserId, AuditAction action, string? filePath, string resultDetail, CancellationToken ct) =>
        _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = actingUserId,
            TabModule = TabModule,
            Action = action,
            FilePath = filePath,
            Result = AuditResult.Success,
            ResultDetail = resultDetail
        }, ct);
}
