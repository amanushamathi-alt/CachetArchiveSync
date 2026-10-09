using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB round-trip tests for <c>dbo.odrive_bucket_mapping</c> and its stored procedures
/// (<c>_ins</c>/<c>_get</c>), introduced by CAS-011 §7 (migration
/// <c>Database/migrations/20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-
/// config.sql</c>). Per CAS-011 §13 ("Resolved 2026-09-22: the IOdriveBucketMappingRepository/
/// OdriveBucketMappingRepository pair is deferred to CAS-012/CAS-013, not authored as part of
/// /implement CAS-011"), no repository class exists yet for this table — these tests call the
/// stored procedures directly via Dapper against a raw <see cref="SqlConnection"/>, matching the
/// pattern <see cref="SearchResultRepositoryTests"/> uses for procedure-level seeding.
///
/// Skip-marked in this sandbox, same reason and setup convention as <see cref="AuditLogRepositoryTests"/>
/// / <see cref="SearchResultRepositoryTests"/>: no live SQL Server instance is reachable here. Un-skip
/// and run manually against a real instance with the CAS-011 migration deployed before merging.
/// </summary>
public sealed class OdriveBucketMappingRepositoryTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database with the " +
        "CAS-011 migration (20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-" +
        "config.sql) applied — not available in this sandbox; run manually against a real instance " +
        "before merging.";

    private readonly string _connectionString;

    public OdriveBucketMappingRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
    }

    private async Task<int> InsertMappingAsync(
        SqlConnection connection,
        string bucketName,
        string odriveRootPath,
        string b2MirrorRootPath)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@odrive_root_path", odriveRootPath);
        parameters.Add("@b2_mirror_root_path", b2MirrorRootPath);
        parameters.Add("@odrive_bucket_mapping_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.odrive_bucket_mapping_ins",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure));

        return parameters.Get<int>("@odrive_bucket_mapping_id");
    }

    private async Task<MappingRow?> GetMappingAsync(SqlConnection connection, string bucketName)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@bucket_name", bucketName);

        return await connection.QuerySingleOrDefaultAsync<MappingRow>(new CommandDefinition(
            "dbo.odrive_bucket_mapping_get",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure));
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC2_OdriveBucketMappingRoundTripAndUniqueness()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var bucketName = "cas011-sc2-" + Guid.NewGuid().ToString("N")[..8];
        const string odriveRootPath = @"D:\cachetServer-C\D-drive";
        const string b2MirrorRootPath = @"D:\b2-mirror\CachetServer-C";

        // Act — CAS-011 §10 SC-2: _ins followed by _get round-trips bucket_name/odrive_root_path/
        // b2_mirror_root_path correctly.
        var mappingId = await InsertMappingAsync(connection, bucketName, odriveRootPath, b2MirrorRootPath);
        var row = await GetMappingAsync(connection, bucketName);

        // Assert — round-trip
        mappingId.Should().BeGreaterThan(0);
        row.Should().NotBeNull();
        row!.odrive_bucket_mapping_id.Should().Be(mappingId);
        row.bucket_name.Should().Be(bucketName);
        row.odrive_root_path.Should().Be(odriveRootPath);
        row.b2_mirror_root_path.Should().Be(b2MirrorRootPath);
        row.is_active.Should().BeTrue();

        // Act — a second insert of an active row with the same bucket_name must fail on
        // UQ_odrive_bucket_mapping_bucket_name (the filtered unique index, CAS-011 §7).
        var act = async () => await InsertMappingAsync(connection, bucketName, odriveRootPath, b2MirrorRootPath);

        // Assert
        await act.Should().ThrowAsync<SqlException>();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_V3_OdriveBucketMappingUniqueBucketName()
    {
        // Arrange — CAS-011 §9 V3: dbo.odrive_bucket_mapping.bucket_name must be unique among
        // active (is_active = 1) rows.
        await using var connection = new SqlConnection(_connectionString);
        var bucketName = "cas011-v3-" + Guid.NewGuid().ToString("N")[..8];
        await InsertMappingAsync(connection, bucketName, @"D:\odrive-root", @"D:\b2-mirror-root");

        // Act
        var act = async () => await InsertMappingAsync(connection, bucketName, @"D:\other-odrive-root", @"D:\other-b2-mirror-root");

        // Assert — the filtered unique index rejects the duplicate active bucket_name.
        await act.Should().ThrowAsync<SqlException>();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_V4_OdriveBucketMappingRequiredFields()
    {
        // Arrange — CAS-011 §9 V4: bucket_name, odrive_root_path, and b2_mirror_root_path are all
        // required. dbo.odrive_bucket_mapping_ins RAISERRORs (severity 16) for each missing field,
        // matching dbo.file_index_ins's empty-string guard pattern.
        await using var connection = new SqlConnection(_connectionString);

        var missingBucketName = async () => await InsertMappingAsync(connection, string.Empty, @"D:\odrive-root", @"D:\b2-mirror-root");
        var missingOdriveRoot = async () => await InsertMappingAsync(connection, "cas011-v4-" + Guid.NewGuid().ToString("N")[..8], string.Empty, @"D:\b2-mirror-root");
        var missingB2MirrorRoot = async () => await InsertMappingAsync(connection, "cas011-v4-" + Guid.NewGuid().ToString("N")[..8], @"D:\odrive-root", string.Empty);

        // Act / Assert — each required-field guard raises a SqlException (RAISERROR), not a silent
        // success with a NULL/empty value stored.
        await missingBucketName.Should().ThrowAsync<SqlException>();
        await missingOdriveRoot.Should().ThrowAsync<SqlException>();
        await missingB2MirrorRoot.Should().ThrowAsync<SqlException>();
    }

    private sealed record MappingRow(
        int odrive_bucket_mapping_id,
        string bucket_name,
        string odrive_root_path,
        string b2_mirror_root_path,
        bool is_active,
        string created_by,
        DateTime created_at,
        string updated_by,
        DateTime updated_at);
}
