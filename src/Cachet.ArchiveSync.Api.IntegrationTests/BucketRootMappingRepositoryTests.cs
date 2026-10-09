using Cachet.ArchiveSync.Api.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB test for <see cref="BucketRootMappingRepository"/> (CAS-013 §5/§13
/// "BucketRootMappingRepositoryTests" — V1), reading CAS-011's <c>dbo.odrive_bucket_mapping</c> table
/// via <c>dbo.odrive_bucket_mapping_get</c>.
///
/// Skip-marked in this sandbox, same reason and setup convention as
/// <see cref="OdriveBucketMappingRepositoryTests"/> / <see cref="AuditLogRepositoryTests"/>: no live
/// SQL Server instance is reachable here. Un-skip and run manually against a real instance with the
/// CAS-011 migration (<c>20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-
/// config.sql</c>) applied before merging — this is the same accepted structural gap CAS-007/CAS-013
/// §13's coverage-gate note already documents and R. Devarajan has accepted.
/// </summary>
public sealed class BucketRootMappingRepositoryTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database with the " +
        "CAS-011 migration (20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-" +
        "config.sql) applied — not available in this sandbox; run manually against a real instance " +
        "before merging.";

    private readonly string _connectionString;

    public BucketRootMappingRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_V1_UnmappedBucketFailsThatFileLocalReadNotWholeBatch()
    {
        // Arrange — a bucket_name guaranteed to have no active dbo.odrive_bucket_mapping row.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = _connectionString })
            .Build();
        var sut = new BucketRootMappingRepository(configuration);
        var unmappedBucketName = "cas013-v1-unmapped-" + Guid.NewGuid().ToString("N")[..8];

        // Act — CAS-013 §5: IBucketRootMappingRepository.GetRootPathAsync returns null (does not
        // throw) for an unmapped bucket; ODriveFileReader (proven separately in
        // ODriveFileReaderTests.Test_V1_UnmappedBucketFailsThatFileLocalReadNotWholeBatch) is the
        // layer that turns this null into the clear, non-path-leaking exception the caller's
        // SyncActionFailedLocal per-file failure path catches — this test proves the repository's
        // half of that contract against the real CAS-011 schema.
        var rootPath = await sut.GetRootPathAsync(unmappedBucketName, CancellationToken.None);

        // Assert
        rootPath.Should().BeNull();
    }
}
