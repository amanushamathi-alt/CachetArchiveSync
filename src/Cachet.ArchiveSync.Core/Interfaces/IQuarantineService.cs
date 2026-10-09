using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="IQuarantineService.AddAsync"/> (CAS-006 §6.3).</summary>
public enum QuarantineAddOutcome
{
    /// <summary>At least one file was added — <c>200 OK</c>.</summary>
    Added,

    /// <summary><c>fileIds</c> was empty (§7 V6) — <c>422 validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary>Unknown <c>taskId</c> — <c>404 task-not-found</c>.</summary>
    TaskNotFound,

    /// <summary>A requested <c>fileId</c> does not resolve to a <c>search_result</c> row scoped to <c>taskId</c> (§7 V7) — <c>404 file-not-found</c>.</summary>
    FileNotFound,

    /// <summary>Every requested file already has an <c>Active</c> quarantine entry (§7 V8) — <c>409 file-already-quarantined</c>.</summary>
    AllAlreadyQuarantined
}

/// <summary>Result of one <see cref="IQuarantineService.AddAsync"/> call.</summary>
public sealed record QuarantineAddResult
{
    public required QuarantineAddOutcome Outcome { get; init; }

    public QuarantineAddResponse? Response { get; init; }
}

/// <summary>
/// <c>POST /search/results/quarantine</c> validate+add (CAS-006 §6.3, FR-09). Writes the
/// <c>QuarantineAdd</c> audit row before returning on any successful (partial or full) add (§12)
/// — the caller does not need to audit this call itself.
/// </summary>
public interface IQuarantineService
{
    Task<QuarantineAddResult> AddAsync(QuarantineAddRequest request, int actingUserId, CancellationToken ct);
}
