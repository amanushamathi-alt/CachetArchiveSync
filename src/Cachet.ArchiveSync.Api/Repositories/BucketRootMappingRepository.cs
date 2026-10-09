using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IBucketRootMappingRepository"/> (CAS-013 §5), reading
/// CAS-011's <c>dbo.odrive_bucket_mapping</c> table via <c>dbo.odrive_bucket_mapping_get</c> and
/// projecting only <c>odrive_root_path</c> — this spec's Sync actions never read a local
/// B2-mirror file (CAS-013 §0/§7 reconciliation note).
/// </summary>
public sealed class BucketRootMappingRepository : IBucketRootMappingRepository
{
    private readonly string _connectionString;

    public BucketRootMappingRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<string?> GetRootPathAsync(string bucketName, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@bucket_name", bucketName);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "dbo.odrive_bucket_mapping_get",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        return row?.odrive_root_path;
    }

    // Property-based (not a positional record): dbo.odrive_bucket_mapping_get returns all nine
    // mapping columns, and Dapper binds a positional constructor only when every column matches by
    // position and name. Properties bind by name and ignore the unused columns.
    private sealed class Row
    {
        public string odrive_root_path { get; set; } = string.Empty;
    }
}
