using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IBucketMirrorRootRepository"/> (CAS-012 §5), reading
/// CAS-011's <c>dbo.odrive_bucket_mapping</c> table via <c>dbo.odrive_bucket_mapping_get</c>.
/// </summary>
public sealed class BucketMirrorRootRepository : IBucketMirrorRootRepository
{
    private readonly string _connectionString;

    public BucketMirrorRootRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<string> GetRootPathAsync(string bucketName, string source, CancellationToken ct)
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
            // Non-path-leaking (no path value included) — caller (CAS-012 §7 V10) treats this as
            // an unreadable-file skip, not a task failure.
            throw new InvalidOperationException(
                "No active odrive_bucket_mapping row found for the requested bucket.");
        }

        return source switch
        {
            "ODrive" => row.odrive_root_path,
            "B2" => row.b2_mirror_root_path,
            _ => throw new InvalidOperationException($"Unrecognized mirror source '{source}'.")
        };
    }

    // Property-based (not a positional record): dbo.odrive_bucket_mapping_get returns all nine
    // mapping columns, and Dapper binds a positional constructor only when every column matches by
    // position and name. Properties bind by name and ignore the unused columns.
    private sealed class Row
    {
        public string odrive_root_path { get; set; } = string.Empty;

        public string b2_mirror_root_path { get; set; } = string.Empty;
    }
}
