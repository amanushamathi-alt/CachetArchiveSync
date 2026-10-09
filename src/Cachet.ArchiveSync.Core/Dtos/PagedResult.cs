namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Generic pagination envelope matching API spec §1.4's convention
/// (<c>items</c>/<c>page</c>/<c>pageSize</c>/<c>totalCount</c>) — reused by both
/// <c>filenameMatches</c> and <c>contentMatches</c> panels (CAS-006 §6.2). No prior paging DTO
/// existed in this codebase (checked <c>Core/Dtos</c> before adding).
/// </summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int TotalCount { get; init; }
}
