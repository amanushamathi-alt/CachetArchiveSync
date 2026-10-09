namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One item of <c>GET /audit-log</c>'s <c>200 OK</c> <c>items</c> array (CAS-009 §6, FR-23/FR-24).
/// Field names/shape match the API design doc §8.1 example exactly.
/// </summary>
public sealed record AuditLogListItem
{
    public required string User { get; init; }

    public required string Tab { get; init; }

    public required string Action { get; init; }

    public string? FilePath { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Result { get; init; }

    public string? ResultDetail { get; init; }
}
