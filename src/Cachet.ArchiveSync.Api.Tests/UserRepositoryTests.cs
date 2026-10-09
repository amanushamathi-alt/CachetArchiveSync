using Cachet.ArchiveSync.Api.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit-testable slice of <see cref="UserRepository"/> (CAS-002 §6.1 step 1 / §6.3a; CAS-018 §2
/// step 1 for <see cref="UserRepository.GetByIdAsync"/>): the constructor's
/// configuration-validation branch — shared by every method on this class, since they all open a
/// connection from the same <c>_connectionString</c> field. <see cref="UserRepository.GetByUsernameAsync"/>,
/// <see cref="UserRepository.GetByIdAsync"/>, <see cref="UserRepository.GetSessionTokenVersionAsync"/>,
/// and <see cref="UserRepository.IncrementSessionTokenVersionAsync"/> each open a real
/// <c>SqlConnection</c> and call a stored procedure — those bodies require a live SQL Server and
/// are covered instead by the <c>[Fact(Skip = ...)]</c>-marked tests in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.UserRepositoryDatabaseTests</c>, following this
/// project's <c>AuditLogRepositoryTests</c> convention for real-DB-only coverage.
/// </summary>
public sealed class UserRepositoryTests
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
        var act = () => new UserRepository(configuration);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Test_Construct_MissingConnectionString_Throws()
    {
        // Arrange — no ConnectionStrings:CachetArchiveSync key at all.
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var act = () => new UserRepository(configuration);

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
        var act = () => new UserRepository(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
