namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /search</c> request body (CAS-006 §6). <see cref="Bucket"/> must equal the configured
/// <c>B2:BucketName</c> exactly (§7 V2); <see cref="Extension"/> must be one of
/// <c>All|PDF|DOC|DOCX|XLS|XLSX|TXT</c> (§7 V3). <see cref="Term"/> must be present in the body.
/// It must be non-empty after trim and at most <c>Search:MaxTermLength</c> characters (§7 V1),
/// except that CAS-038 A2 allows it to be empty/blank when both dates are set (a date-only search,
/// range capped at 31 days, §9 V14). It is never logged verbatim (§11 — a search term can be
/// sensitive/privileged text). <see cref="FromDate"/> / <see cref="ToDate"/> (CAS-038 §2/§8) are an
/// optional <c>created_at</c> range (ISO <c>yyyy-MM-dd</c>, whole UTC days, inclusive). Both null
/// means a keyword-only search. Otherwise both must be set (§9 V11), From on or before To (§9 V12),
/// and neither after today UTC (§9 V13).
/// </summary>
public sealed record SearchRequest
{
    public required string Term { get; init; }

    public required string Bucket { get; init; }

    public required string Extension { get; init; }

    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    /// <summary>
    /// CAS-047 §2/§9 V1: optional search mode — exactly <c>Filename</c>, <c>Keyword</c> or <c>Date</c>
    /// (case-sensitive). Absent = legacy behavior (V6).
    /// </summary>
    public string? Mode { get; init; }
}
