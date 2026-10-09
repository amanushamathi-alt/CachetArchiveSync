using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for <see cref="BucketMirrorRootRepository"/> (CAS-012 §5/§13
/// "BucketMirrorRootRepositoryTests" — root-path resolution for a known <c>(bucketName, source)</c>
/// pair, and the not-found case), against CAS-011's <c>dbo.odrive_bucket_mapping</c> table via
/// <c>dbo.odrive_bucket_mapping_get</c>. These are real-SQL-round-trip assertions a mocked
/// <see cref="Cachet.ArchiveSync.Core.Interfaces.IBucketMirrorRootRepository"/> cannot prove — only a
/// real database with CAS-011's migration applied can.
///
/// Skip-marked in this sandbox, following the same convention as
/// <see cref="OdriveBucketMappingRepositoryTests"/> / <see cref="FileIndexRepositoryTests"/>: no live
/// SQL Server instance is reachable here. Un-skip and run manually against a real instance with the
/// CAS-011 migration deployed before merging.
/// </summary>
public sealed class BucketMirrorRootRepositoryTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database with the " +
        "CAS-011 migration (20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-" +
        "config.sql) applied — not available in this sandbox; run manually against a real instance " +
        "before merging.";

    private readonly string _connectionString;

    public BucketMirrorRootRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
    }

    private IConfiguration BuildRepositoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CachetArchiveSync"] = _connectionString
            })
            .Build();

    private async Task InsertMappingAsync(SqlConnection connection, string bucketName, string odriveRootPath, string b2MirrorRootPath)
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
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC1_GetRootPathAsync_ResolvesOdriveAndB2RootsForKnownBucket()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var bucketName = "cas012-bmr-sc1-" + Guid.NewGuid().ToString("N")[..8];
        const string odriveRootPath = @"D:\cachetServer-C\D-drive";
        const string b2MirrorRootPath = @"D:\b2-mirror\CachetServer-C";
        await InsertMappingAsync(connection, bucketName, odriveRootPath, b2MirrorRootPath);
        var repository = new BucketMirrorRootRepository(BuildRepositoryConfiguration());

        // Act
        var odriveResolved = await repository.GetRootPathAsync(bucketName, "ODrive", CancellationToken.None);
        var b2Resolved = await repository.GetRootPathAsync(bucketName, "B2", CancellationToken.None);

        // Assert — CAS-012 §5: ODrive selects odrive_root_path, B2 selects b2_mirror_root_path.
        odriveResolved.Should().Be(odriveRootPath);
        b2Resolved.Should().Be(b2MirrorRootPath);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_V10_GetRootPathAsync_NoMappingRowForBucket_ThrowsNonPathLeakingException()
    {
        // Arrange — no dbo.odrive_bucket_mapping row exists for this bucket name.
        var bucketName = "cas012-bmr-no-mapping-" + Guid.NewGuid().ToString("N")[..8];
        var repository = new BucketMirrorRootRepository(BuildRepositoryConfiguration());

        // Act — CAS-012 §5/§7 V10: the caller (MirrorFileReader → KeywordSearchTaskProcessor)
        // treats this as an unreadable-file skip, not a task failure.
        var act = async () => await repository.GetRootPathAsync(bucketName, "B2", CancellationToken.None);

        // Assert — no path value is included in the exception message (§11 — non-path-leaking).
        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().NotContain(@"\").And.NotContain("/");
    }
}
