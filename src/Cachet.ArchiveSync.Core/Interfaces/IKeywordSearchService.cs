using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="IKeywordSearchService.SubmitAsync"/> (CAS-006 §6).</summary>
public enum SearchSubmitOutcome
{
    Accepted,

    /// <summary><c>term</c>/<c>bucket</c>/<c>extension</c> failed §7 V1–V3 — <c>422 validation-failed</c>.</summary>
    ValidationFailed
}

/// <summary>Result of one <see cref="IKeywordSearchService.SubmitAsync"/> call.</summary>
public sealed record SearchSubmitResult
{
    public required SearchSubmitOutcome Outcome { get; init; }

    public SearchSubmittedResponse? Response { get; init; }

    /// <summary>Populated only when <see cref="Outcome"/> is <see cref="SearchSubmitOutcome.ValidationFailed"/>.</summary>
    public string? ValidationError { get; init; }
}

/// <summary>Outcome of <see cref="IKeywordSearchService.GetResultsAsync"/> (CAS-006 §6.2).</summary>
public enum SearchResultsOutcome
{
    Found,

    /// <summary>Unknown <c>taskId</c>, or <c>taskId</c> belongs to a different <c>task_type</c> (§7 V4) — <c>404 task-not-found</c>.</summary>
    TaskNotFound,

    /// <summary>Task is <c>Queued</c>/<c>Running</c> (§7 V5) — <c>409 task-not-complete</c>.</summary>
    TaskNotComplete
}

/// <summary>Result of one <see cref="IKeywordSearchService.GetResultsAsync"/> call.</summary>
public sealed record SearchResultsQueryResult
{
    public required SearchResultsOutcome Outcome { get; init; }

    public SearchResultsResponse? Response { get; init; }
}

/// <summary>
/// <c>POST /search</c> validate+submit and <c>GET /search/{taskId}/results</c> read (CAS-006 §6,
/// §6.2). <see cref="SubmitAsync"/> writes the <c>KeywordSearchSubmitted</c> audit row before
/// returning (§12) — the caller does not need to audit this call itself.
/// </summary>
public interface IKeywordSearchService
{
    Task<SearchSubmitResult> SubmitAsync(SearchRequest request, int submittedByUserId, CancellationToken ct);

    Task<SearchResultsQueryResult> GetResultsAsync(int taskId, int page, int pageSize, CancellationToken ct);
}
