using Cachet.ArchiveSync.Api.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit-testable slice of <see cref="BucketMirrorRootRepository"/> (CAS-012 §5/§13
/// "BucketMirrorRootRepositoryTests"): the constructor's configuration-validation branch, following
/// this project's established <see cref="UserRepositoryTests"/> convention for Dapper/SQL-backed
/// repositories. <see cref="BucketMirrorRootRepository.GetRootPathAsync"/> itself opens a real
/// <c>SqlConnection</c> and calls <c>dbo.odrive_bucket_mapping_get</c> — its root-path-resolution and
/// not-found (§13) behavior is covered instead by the <c>[Fact(Skip = ...)]</c>-marked tests in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.BucketMirrorRootRepositoryTests</c>.
/// </summary>
public sealed class BucketMirrorRootRepositoryTests
{
    private static IConfiguration CreateConfiguration(string? connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CachetArchiveSync"] = connectionString
            })
            .Build();

    [Fact]
    public void Test_Construct_ConnectionStringPresent_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateConfiguration("Server=(localdb)\\MSSQLLocalDB;Database=CachetArchiveSync;Trusted_Connection=True;");

        // Act
        var act = () => new BucketMirrorRootRepository(configuration);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Test_Construct_MissingConnectionString_Throws()
    {
        // Arrange — no ConnectionStrings:CachetArchiveSync key at all.
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var act = () => new BucketMirrorRootRepository(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:CachetArchiveSync*");
    }

    [Fact]
    public void Test_Construct_BlankConnectionString_Throws()
    {
        // Arrange — GetConnectionString returns null (not empty-string-passthrough) when the key
        // itself is absent; this covers the case where the key exists but is set to an empty value.
        var configuration = CreateConfiguration(connectionString: null);

        // Act
        var act = () => new BucketMirrorRootRepository(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
