namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Read-side projection of one row returned by <c>dbo.configuration_detail_lst</c> (CAS-041 §2/§7),
/// consumed only by <see cref="Configuration.DatabaseConfigurationSource"/>. Property-based (not a
/// positional record): the procedure returns eleven columns, and Dapper binds a positional
/// constructor only when every column matches; properties bind by name and ignore the rest.
/// </summary>
public sealed class ConfigurationDetailRow
{
    public string ConfigurationDetailName { get; init; } = string.Empty;

    public string? ConfigurationValue1 { get; init; }
}
