namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Converts a <c>DATETIME2</c> value Dapper maps as <see cref="DateTime"/> (SQL Server has no
/// native offset-aware datetime2 mapping) into the <see cref="DateTimeOffset"/> this project's
/// Core DTOs use for timestamps. Every such column is written via <c>SYSUTCDATETIME()</c>
/// (CAS-003 convention), so the value is always UTC. Extracted here once this conversion was
/// needed in three repositories (<see cref="SearchResultRepository"/>, <see cref="TaskQueueRepository"/>,
/// <see cref="FileIndexRepository"/>) — not a speculative abstraction (P4).
/// </summary>
internal static class SqlDateTimeMapping
{
    internal static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    internal static DateTimeOffset? ToUtcOffsetOrNull(DateTime? value) =>
        value is null ? null : ToUtcOffset(value.Value);
}
