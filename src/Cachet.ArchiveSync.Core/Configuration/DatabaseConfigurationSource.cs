using Cachet.ArchiveSync.Core.Dtos;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Core.Configuration;

/// <summary>
/// CAS-041 §2: configuration source that reads allow-listed settings from
/// <c>dbo.configuration_detail</c> (via <c>dbo.configuration_detail_lst</c>) once at host startup.
/// Added last to <c>builder.Configuration</c>, so a non-blank database value overrides the
/// environment variable for the same key; a missing row or blank value falls through to it.
/// The SQL call itself is supplied by the host (<see cref="LoadGroup"/>) so Core carries no
/// data-access dependency.
/// </summary>
public sealed class DatabaseConfigurationSource : IConfigurationSource
{
    /// <summary>Connection string; when null/blank the source is skipped (§2 item 4).</summary>
    public string? ConnectionString { get; init; }

    /// <summary><c>ConfigurationDatabase:Enabled</c>; <c>false</c> skips the source (§2 item 4).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>(connectionString, configurationName) → rows of that configuration group.</summary>
    public required Func<string, string, IReadOnlyList<ConfigurationDetailRow>> LoadGroup { get; init; }

    /// <summary>
    /// Configuration group name → the <c>configuration_detail_name</c> values this process may load
    /// (§2 item 2). Rows not listed here are ignored even if the group returns them.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllowedNamesByGroup { get; init; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new DatabaseConfigurationProvider(this);
}

/// <summary>
/// CAS-041 §2 provider for <see cref="DatabaseConfigurationSource"/>. Never logs values, and
/// failure messages name the configuration group only (§11).
/// </summary>
public sealed class DatabaseConfigurationProvider : ConfigurationProvider
{
    private readonly DatabaseConfigurationSource _source;

    public DatabaseConfigurationProvider(DatabaseConfigurationSource source)
    {
        _source = source;
    }

    public override void Load()
    {
        if (!_source.Enabled || string.IsNullOrWhiteSpace(_source.ConnectionString))
        {
            return;
        }

        foreach (var (group, allowedNames) in _source.AllowedNamesByGroup)
        {
            IReadOnlyList<ConfigurationDetailRow> rows;
            try
            {
                rows = _source.LoadGroup(_source.ConnectionString, group);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"CAS-041: failed to load configuration group '{group}' via dbo.configuration_detail_lst.",
                    ex);
            }

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ConfigurationValue1)
                    || !allowedNames.Contains(row.ConfigurationDetailName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Same convention as environment variables: "B2__SystemKeyId" → "B2:SystemKeyId".
                var key = row.ConfigurationDetailName.Replace("__", ConfigurationPath.KeyDelimiter);
                Data[key] = row.ConfigurationValue1;
            }
        }
    }
}
