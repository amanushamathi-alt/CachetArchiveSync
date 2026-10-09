using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IB2PathPrefixProvider"/>: reads
/// <c>dbo.odrive_bucket_mapping.b2_path_prefix</c> through <c>dbo.odrive_bucket_mapping_get</c>,
/// the same shape as <see cref="BucketRootMappingRepository"/>.
/// </summary>
public sealed class B2PathPrefixProvider : IB2PathPrefixProvider
{
    private readonly string _connectionString;

    public B2PathPrefixProvider(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<string?> GetPrefixAsync(string bucketName, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@bucket_name", bucketName);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "dbo.odrive_bucket_mapping_get",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        // Accept either slash style and stray leading/trailing separators (NULL/blank = bucket root).
        return (row.b2_path_prefix ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
    }

    // Property-based: the SP returns every mapping column; only the prefix is read here.
    private sealed class Row
    {
        public string? b2_path_prefix { get; set; }
    }
}
