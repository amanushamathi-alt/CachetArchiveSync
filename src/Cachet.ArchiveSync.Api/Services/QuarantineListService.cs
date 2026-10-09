using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IQuarantineListService"/> implementation of CAS-008 §6 — <c>GET /quarantine</c>
/// (FR-14). A plain read; not audited (§12 only names <c>POST /quarantine</c> and
/// <c>POST /quarantine/delete</c> as audited endpoints).
/// </summary>
public sealed class QuarantineListService : IQuarantineListService
{
    private readonly IQuarantineEntryRepository _quarantineEntryRepository;
    private readonly ILogger<QuarantineListService> _logger;

    public QuarantineListService(
        IQuarantineEntryRepository quarantineEntryRepository,
        ILogger<QuarantineListService> logger)
    {
        _quarantineEntryRepository = quarantineEntryRepository;
        _logger = logger;
    }

    public async Task<QuarantineListResponse> GetActiveAsync(int actingUserId, int page, int pageSize, CancellationToken ct)
    {
        var listPage = await _quarantineEntryRepository.ListActivePagedAsync(page, pageSize, ct);

        var items = listPage.Rows.Select(row => new QuarantineListItem
        {
            FileId = row.QuarantineEntryId,
            FileIndexId = row.FileIndexId,
            Name = row.Name,
            Path = row.Path,
            QuarantinedOn = row.QuarantinedOn,
            Status = row.Status,
            SourceTab = row.SourceTab
        }).ToList();

        _logger.LogInformation(
            "Quarantine list requested by user {UserId}: page {Page} ({PageSize}/page), {ItemCount} of {TotalCount} active entries",
            actingUserId, page, pageSize, items.Count, listPage.TotalCount);

        return new QuarantineListResponse
        {
            Items = items,
            TotalCount = listPage.TotalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}
