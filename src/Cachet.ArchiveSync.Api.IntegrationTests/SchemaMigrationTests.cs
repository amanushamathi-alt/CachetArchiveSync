using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB idempotency test for the CAS-011 migration
/// (<c>Database/migrations/20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-
/// config.sql</c>), per CAS-011 §10 SC-3 ("Re-running the new migration is a no-op (idempotent),
/// consistent with CAS-003's SC-1 idempotency guarantee for the original schema") and §13 Test Plan
/// ("SchemaMigrationTests (existing class per CAS-003 §13, extend)").
///
/// No <c>SchemaMigrationTests</c> class exists yet in this repository (confirmed by a repo-wide
/// search before authoring this file) — this project has no C# migration-runner tool (no DbUp or
/// equivalent found), so "re-running the migration" is proven here by reading the migration's own
/// `.sql` file and executing its batches (split on `GO`, since <see cref="SqlConnection"/>/Dapper do
/// not understand the `GO` batch separator natively — that is a `sqlcmd`/SSMS-only convention) twice
/// in sequence against a real database. This is a new class, not an extension of a pre-existing one,
/// since none exists; it is written so any subsequent migration idempotency test can extend it the
/// way CAS-011 intended to extend CAS-003's.
///
/// Skip-marked in this sandbox, same reason and setup convention as <see cref="AuditLogRepositoryTests"/>:
/// no live SQL Server instance is reachable here. Un-skip and run manually against a real instance
/// before merging.
/// </summary>
public sealed class SchemaMigrationTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database — not " +
        "available in this sandbox; run manually against a real instance before merging.";

    private const string MigrationFileName =
        "20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-config.sql";

    private readonly string _connectionString;

    public SchemaMigrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
    }

    /// <summary>
    /// Locates the CAS-011 migration file relative to the test assembly's output directory by
    /// walking up to the repository root (this integration-test project has no existing
    /// "copy migrations to output" build step, so the file is read from its source location).
    /// </summary>
    private static string ResolveMigrationFilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Database", "migrations", MigrationFileName)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException(
                $"Could not locate {MigrationFileName} by walking up from {AppContext.BaseDirectory}. " +
                "Expected to find it under <repo-root>/Database/migrations/.");
        }

        return Path.Combine(directory.FullName, "Database", "migrations", MigrationFileName);
    }

    /// <summary>
    /// Splits a `.sql` migration file's text into batches on a line containing only `GO`
    /// (case-insensitive, optional surrounding whitespace) — the same convention SSMS/`sqlcmd` use,
    /// since <see cref="SqlConnection"/> executes one batch at a time and does not understand `GO`
    /// itself.
    /// </summary>
    private static IEnumerable<string> SplitIntoBatches(string script) =>
        Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0);

    private async Task RunMigrationAsync(SqlConnection connection)
    {
        var script = await File.ReadAllTextAsync(ResolveMigrationFilePath());

        foreach (var batch in SplitIntoBatches(script))
        {
            await connection.ExecuteAsync(batch);
        }
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC3_MigrationIsIdempotent()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Act — run the CAS-011 migration twice in sequence. Every DDL statement in the migration
        // is guarded (IF EXISTS / OBJECT_ID checks for the DROP FULLTEXT INDEX/CATALOG and the
        // CREATE TABLE dbo.odrive_bucket_mapping, and CREATE OR ALTER for every stored procedure),
        // so a second run must not throw and must leave the schema in the same end state as after
        // the first run (CAS-011 §10 SC-3; CAS-003 §10 SC-1 precedent).
        var firstRun = async () => await RunMigrationAsync(connection);
        await firstRun.Should().NotThrowAsync();

        var secondRun = async () => await RunMigrationAsync(connection);

        // Assert — no-op: the second run does not throw.
        await secondRun.Should().NotThrowAsync();

        // Assert — end state after the second run matches the intended post-migration schema.
        var contentTextColumnCount = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM sys.columns
              WHERE object_id = OBJECT_ID(N'dbo.file_index') AND name = N'content_text';");
        contentTextColumnCount.Should().Be(0);

        var fullTextCatalogCount = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM sys.fulltext_catalogs WHERE name = N'cachet_ft_catalog';");
        fullTextCatalogCount.Should().Be(0);

        var mappingTableExists = await connection.ExecuteScalarAsync<int>(
            @"SELECT CASE WHEN OBJECT_ID(N'dbo.odrive_bucket_mapping', N'U') IS NOT NULL THEN 1 ELSE 0 END;");
        mappingTableExists.Should().Be(1);

        var mappingProcedureCount = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM sys.procedures
              WHERE name IN (
                  N'odrive_bucket_mapping_ins', N'odrive_bucket_mapping_get',
                  N'odrive_bucket_mapping_lst', N'odrive_bucket_mapping_upd',
                  N'odrive_bucket_mapping_del');");
        mappingProcedureCount.Should().Be(5);
    }
}
