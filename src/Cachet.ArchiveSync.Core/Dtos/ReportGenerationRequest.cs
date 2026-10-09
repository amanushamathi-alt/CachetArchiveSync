namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Shared request shape for <c>POST /reports/pre-action</c> and <c>POST /reports/post-action</c>
/// (CAS-010 §6.1/§6.2; CAS-015 §8/§9 V7). <see cref="ActiveTabLabel"/> is optional and cosmetic
/// only — "a label, not a filter" (API design doc §9.1) — it never filters the rendered rows.
/// <see cref="Format"/> is required and selects the single file format rendered and returned in the
/// response body — must be exactly <c>"pdf"</c> or <c>"xlsx"</c> (case-sensitive); any other value
/// (missing, empty, unrecognized) is a <c>400</c> validation error, never a silent default (CAS-015
/// §9 V7).
/// </summary>
public sealed record ReportGenerationRequest
{
    public string? ActiveTabLabel { get; init; }

    public required string Format { get; init; }
}
