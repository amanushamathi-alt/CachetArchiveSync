using System.Text.Json.Serialization;

namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>One file directly inside the listed folder (CAS-043 §6).</summary>
public sealed record BrowseFileItem
{
    public required int FileIndexId { get; init; }

    public required string Name { get; init; }

    /// <summary>Lower-case, no leading dot; empty when the index row has none.</summary>
    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    /// <summary>CAS-049 — set only by the search-results browse; omitted from JSON for <c>/files/browse</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SearchResultId { get; init; }

    /// <summary>CAS-049 — the match's status; omitted from JSON for <c>/files/browse</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchStatus { get; init; }
}
