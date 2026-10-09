using Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for CAS-011 §9/§10 ("FileIndexRepositoryTests" per §13 Test Plan) — proves the
/// migration <c>Database/migrations/20260922_210000__CAS-011-file-index-content-removal-and-path-
/// mapping-config.sql</c> actually removed <c>dbo.file_index.content_text</c>, the
/// <c>cachet_ft_catalog</c> full-text catalog, and the <c>dbo.file_index_search</c> content-match
/// branch. These are schema-introspection / stored-procedure-shape assertions that a mocked
/// <see cref="Cachet.ArchiveSync.Core.Interfaces.IFileIndexRepository"/> cannot prove — only a real
/// database with the CAS-011 migration applied can.
///
/// Skip-marked in this sandbox, following the exact convention established by
/// <see cref="AuditLogRepositoryTests"/> / <see cref="SearchResultRepositoryTests"/>: no live SQL
/// Server instance is reachable here. Connection string read from the
/// <c>ConnectionStrings:CachetArchiveSync</c> configuration key (environment variable
/// <c>ConnectionStrings__CachetArchiveSync</c> or a local <c>appsettings.IntegrationTests.json</c>,
/// neither of which exists yet in this sandbox). Un-skip and run manually against a real instance
/// with the CAS-011 migration deployed before merging.
/// </summary>
[Collection("CAS-047 file_index real-DB")]
public sealed class FileIndexRepositoryTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database with the " +
        "CAS-011 migration (20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-" +
        "config.sql) applied — not available in this sandbox; run manually against a real instance " +
        "before merging.";

    private readonly string _connectionString;

    public FileIndexRepositoryTests()
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

    [Fact(Skip = SkipReason)]
    public async Task Test_SC1_ContentTextAndFullTextCatalogRemoved()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);

        // Act — CAS-011 §10 SC-1: after the migration runs, dbo.file_index has no content_text
        // column and cachet_ft_catalog no longer exists.
        var contentTextColumnCount = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM sys.columns
              WHERE object_id = OBJECT_ID(N'dbo.file_index') AND name = N'content_text';");

        var fullTextCatalogCount = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM sys.fulltext_catalogs WHERE name = N'cachet_ft_catalog';");

        // Assert
        contentTextColumnCount.Should().Be(0);
        fullTextCatalogCount.Should().Be(0);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_V1_ContentMatchTypeReturnsEmpty()
    {
        // Arrange — CAS-011 §9 V1: dbo.file_index_search with @match_type = 'Content' returns zero
        // rows, not an error, once the CONTAINS/CHARINDEX/SUBSTRING content-match branch is removed.
        // dbo.file_index_search's post-migration signature (@bucket_name, @file_extension, @term,
        // @match_type) is called directly since IFileIndexRepository.SearchCandidatesAsync does not
        // expose @match_type as a parameter (it always passes the procedure's default, NULL).
        await using var connection = new SqlConnection(_connectionString);
        var parameters = new DynamicParameters();
        parameters.Add("@bucket_name", "cas011-test-bucket-" + Guid.NewGuid().ToString("N")[..8]);
        parameters.Add("@file_extension", (string?)null);
        parameters.Add("@term", "anything");
        parameters.Add("@match_type", "Content");

        // Act
        var rows = await connection.QueryAsync(new CommandDefinition(
            "dbo.file_index_search",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure));

        // Assert — zero rows, and no exception was thrown getting here.
        rows.Should().BeEmpty();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_V2_ContentTextColumnDoesNotExist()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);

        // Act — CAS-011 §9 V2: schema-introspection test against sys.columns; an INSERT/UPDATE
        // naming content_text would fail to compile/parse once the column is gone.
        var columnExists = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM sys.columns
              WHERE object_id = OBJECT_ID(N'dbo.file_index') AND name = N'content_text';");

        // Assert
        columnExists.Should().Be(0);

        // Confirm the repository-level effect too: FileIndexRepository.InsertAsync's parameter
        // list (CAS-011 §5 mechanical edit) no longer includes @content_text, so a real insert
        // through the repository succeeds without binding that parameter.
        var repository = new FileIndexRepository(BuildRepositoryConfiguration());
        var fileIndexId = await repository.InsertAsync(
            createdBy: "test-fixture",
            updatedBy: "test-fixture",
            source: "B2",
            bucketName: "cas011-test-bucket",
            relativePath: $"cas011/{Guid.NewGuid():N}.txt",
            fileName: "test.txt",
            fileExtension: "txt",
            fileSizeBytes: 1,
            modifiedAt: DateTimeOffset.UtcNow,
            sha1Hash: null,
            lastSeenAt: null,
            ct: CancellationToken.None);

        fileIndexId.Should().BeGreaterThan(0);
    }

    private const string Cas012SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database with the " +
        "CAS-012 migration (20260922_220000__CAS-012-keyword-search-filesystem-content-search.sql) " +
        "applied — not available in this sandbox; run manually against a real instance before merging.";

    [Fact(Skip = Cas012SkipReason)]
    public async Task Test_SC1_SearchCandidatesAsync_MapsSourceAndBucketNameOntoCandidateRow()
    {
        // Arrange — CAS-012 §5: dbo.file_index_search now projects fi.source/fi.bucket_name on
        // both branches; FileIndexRepository.SearchCandidatesAsync must round-trip both columns
        // onto FileIndexCandidateRow.Source/BucketName so the Worker can resolve a mirror path.
        var repository = new FileIndexRepository(BuildRepositoryConfiguration());
        var bucketName = "cas012-fir-sc1-" + Guid.NewGuid().ToString("N")[..8];
        var relativePath = $"cas012/{Guid.NewGuid():N}-confidential-settlement.txt";

        await repository.InsertAsync(
            createdBy: "test-fixture",
            updatedBy: "test-fixture",
            source: "B2",
            bucketName: bucketName,
            relativePath: relativePath,
            fileName: "confidential-settlement.txt",
            fileExtension: "txt",
            fileSizeBytes: 42,
            modifiedAt: DateTimeOffset.UtcNow,
            sha1Hash: null,
            lastSeenAt: null,
            ct: CancellationToken.None);

        // Act
        var candidates = await repository.SearchCandidatesAsync(bucketName, "txt", "confidential-settlement", null, null, CancellationToken.None);

        // Assert
        candidates.Should().ContainSingle(c => c.RelativePath == relativePath && c.MatchType == "Content")
            .Which.Should().Match<Cachet.ArchiveSync.Core.Dtos.FileIndexCandidateRow>(c =>
                c.Source == "B2" && c.BucketName == bucketName && c.MatchSnippet == null);
    }

    // =================================================================================================
    // CAS-038 §13 "FileIndexRepositoryTests" — dbo.file_index_search optional created_at range
    // (SC-3, SC-4, SC-5). [RealDbFact]: runs whenever ConnectionStrings:CachetArchiveSync is
    // configured, reported Skipped otherwise (never silently passed). Each test applies the CAS-038
    // migration itself first (idempotent CREATE OR ALTER — the self-priming pattern of
    // TaskQueueActiveByTypeGetTests), seeds rows through dbo.file_index_ins in a unique bucket, sets
    // created_at directly (dbo.file_index_ins does not accept it), and deletes its rows afterwards.
    // =================================================================================================

    // CAS-038 A2: all CAS-038 tests self-apply the LATEST definition of dbo.file_index_search (the A2
    // migration — same parameters, a superset of 20260930_120000; identical for a non-empty term), so
    // no test reverts the procedure to the pre-A2 shape and SC-9 is never order-dependent.
    private const string Cas038MigrationFileName = "20260930_150000__CAS-038-A2-date-only-search.sql";
    private const string Cas038Term = "cas038term";

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Database", "migrations")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root (folder containing Database/migrations) not found.");
    }

    // CAS-047: dbo.file_index_search now also takes @source and FileIndexRepository always sends it, so the
    // latest definition (CAS-047) is re-applied after CAS-038's older body.
    private static async Task ApplyCas038MigrationAsync(SqlConnection connection)
    {
        foreach (var migrationFileName in new[] { Cas038MigrationFileName, "20261004_220000__CAS-047-search-modes.sql" })
        {
            var script = (await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "Database", "migrations", migrationFileName)))
            .Replace("USE [CachetArchiveSync];", $"USE [{connection.Database}];");
            var batches = System.Text.RegularExpressions.Regex.Split(
                    script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Where(batch => !string.IsNullOrWhiteSpace(batch));
            foreach (var batch in batches)
            {
                await connection.ExecuteAsync(batch);
            }
        }
    }

    /// <summary>Seeds one active row via dbo.file_index_ins, then sets created_at exactly (DATETIME2(3) literal).</summary>
    private async Task<int> SeedFileIndexRowAsync(SqlConnection connection, string bucketName, string fileName, string createdAtUtc)
    {
        var repository = new FileIndexRepository(BuildRepositoryConfiguration());
        var fileIndexId = await repository.InsertAsync(
            createdBy: "test-fixture",
            updatedBy: "test-fixture",
            source: "B2",
            bucketName: bucketName,
            relativePath: $"cas038/{Guid.NewGuid():N}/{fileName}",
            fileName: fileName,
            fileExtension: "txt",
            fileSizeBytes: 1,
            modifiedAt: DateTimeOffset.UtcNow,
            sha1Hash: null,
            lastSeenAt: null,
            ct: CancellationToken.None);

        // String literal + explicit CAST so the .999 millisecond boundary is never rounded by a
        // DATETIME-typed parameter (Dapper's default DbType for DateTime).
        await connection.ExecuteAsync(
            "UPDATE dbo.file_index SET created_at = CAST(@created_at AS DATETIME2(3)) WHERE file_index_id = @id",
            new { created_at = createdAtUtc, id = fileIndexId });

        return fileIndexId;
    }

    private static Task DeleteBucketRowsAsync(SqlConnection connection, string bucketName) =>
        connection.ExecuteAsync("DELETE FROM dbo.file_index WHERE bucket_name = @bucketName", new { bucketName });

    [RealDbFact]
    public async Task Test_SC3_FileIndexSearchAppliesInclusiveCreatedAtRangeToBothBranches()
    {
        // Arrange — range 2025-01-10 .. 2025-01-12 => [2025-01-10 00:00, 2025-01-13 00:00).
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyCas038MigrationAsync(connection);
        var bucketName = "cas038-sc3-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            // Term in the file name => row appears in BOTH the Filename and the Content branch.
            var fromBoundary = await SeedFileIndexRowAsync(connection, bucketName, $"{Cas038Term}-a.txt", "2025-01-10T00:00:00.000");
            var toBoundary = await SeedFileIndexRowAsync(connection, bucketName, $"{Cas038Term}-b.txt", "2025-01-12T23:59:59.999");
            var midRange = await SeedFileIndexRowAsync(connection, bucketName, $"{Cas038Term}-c.txt", "2025-01-11T12:00:00.000");
            var justBefore = await SeedFileIndexRowAsync(connection, bucketName, $"{Cas038Term}-d.txt", "2025-01-09T23:59:59.999");
            var toPlusOne = await SeedFileIndexRowAsync(connection, bucketName, $"{Cas038Term}-e.txt", "2025-01-13T00:00:00.000");

            // No term in the name/path => Content branch only (unfiltered by term, still date-filtered).
            var contentOnlyInRange = await SeedFileIndexRowAsync(connection, bucketName, "other-f.txt", "2025-01-11T08:00:00.000");
            var contentOnlyToPlusOne = await SeedFileIndexRowAsync(connection, bucketName, "other-g.txt", "2025-01-13T00:00:00.000");

            var repository = new FileIndexRepository(BuildRepositoryConfiguration());

            // Act
            var candidates = await repository.SearchCandidatesAsync(
                bucketName, "All", Cas038Term, new DateOnly(2025, 1, 10), new DateOnly(2025, 1, 12), CancellationToken.None);

            // Assert — Filename branch: exactly the in-range term rows, both boundaries included.
            candidates.Where(c => c.MatchType == "Filename").Select(c => c.FileIndexId)
                .Should().BeEquivalentTo(new[] { fromBoundary, toBoundary, midRange });

            // Assert — Content branch: the same in-range rows plus the in-range term-less row.
            candidates.Where(c => c.MatchType == "Content").Select(c => c.FileIndexId)
                .Should().BeEquivalentTo(new[] { fromBoundary, toBoundary, midRange, contentOnlyInRange });

            // Assert — out-of-range rows (from-1 23:59:59.999, to+1 00:00:00.000) excluded from both branches.
            candidates.Select(c => c.FileIndexId).Should().NotContain(new[] { justBefore, toPlusOne, contentOnlyToPlusOne });
        }
        finally
        {
            await DeleteBucketRowsAsync(connection, bucketName);
        }
    }

    [RealDbFact]
    public async Task Test_SC4_FileIndexSearchWithoutRangeMatchesPriorBehavior()
    {
        // Arrange — rows spread far apart in created_at, including one far in the past.
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyCas038MigrationAsync(connection);
        var bucketName = "cas038-sc4-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            var old = await SeedFileIndexRowAsync(connection, bucketName, $"{Cas038Term}-old.txt", "2019-06-12T09:30:00.000");
            var recent = await SeedFileIndexRowAsync(connection, bucketName, $"{Cas038Term}-recent.txt", "2025-01-11T12:00:00.000");
            var contentOnly = await SeedFileIndexRowAsync(connection, bucketName, "other-h.txt", "2022-03-01T00:00:00.000");

            var repository = new FileIndexRepository(BuildRepositoryConfiguration());

            // Act — (1) the new call path with both dates null;
            var withNullRange = await repository.SearchCandidatesAsync(bucketName, "All", Cas038Term, null, null, CancellationToken.None);

            // (2) the pre-CAS-038 call shape (@bucket_name, @file_extension, @term only — no date params at all).
            var priorShapeParameters = new DynamicParameters();
            priorShapeParameters.Add("@bucket_name", bucketName);
            priorShapeParameters.Add("@file_extension", "All");
            priorShapeParameters.Add("@term", Cas038Term);
            var priorShapeRows = (await connection.QueryAsync(new CommandDefinition(
                    "dbo.file_index_search", priorShapeParameters, commandType: System.Data.CommandType.StoredProcedure)))
                .Select(r => ((int)r.file_index_id, (string)r.match_type))
                .ToList();

            // Assert — identical row set (id + branch) between the null-range call and the prior call shape.
            withNullRange.Select(c => (c.FileIndexId, c.MatchType))
                .Should().BeEquivalentTo(priorShapeRows);

            // Assert — and no created_at filtering is applied: every seeded row is present in its branch(es).
            withNullRange.Where(c => c.MatchType == "Filename").Select(c => c.FileIndexId)
                .Should().BeEquivalentTo(new[] { old, recent });
            withNullRange.Where(c => c.MatchType == "Content").Select(c => c.FileIndexId)
                .Should().BeEquivalentTo(new[] { old, recent, contentOnly });
        }
        finally
        {
            await DeleteBucketRowsAsync(connection, bucketName);
        }
    }

    [RealDbFact]
    public async Task Test_SC5_FileIndexSearchRejectsHalfOpenRange()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyCas038MigrationAsync(connection);
        var bucketName = "cas038-sc5-" + Guid.NewGuid().ToString("N")[..8];
        var repository = new FileIndexRepository(BuildRepositoryConfiguration());

        // Act
        var onlyFrom = async () => await repository.SearchCandidatesAsync(
            bucketName, "All", Cas038Term, new DateOnly(2025, 1, 10), null, CancellationToken.None);
        var onlyTo = async () => await repository.SearchCandidatesAsync(
            bucketName, "All", Cas038Term, null, new DateOnly(2025, 1, 10), CancellationToken.None);

        // Assert — the procedure's defense-in-depth guard raises in both directions.
        (await onlyFrom.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("@created_from and @created_to must be supplied together.");
        (await onlyTo.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("@created_from and @created_to must be supplied together.");
    }

    // ---- CAS-038 A2 SC-9: empty term + range -> every in-range active row as Filename, no Content ----

    [RealDbFact]
    public async Task Test_A2_SC9_FileIndexSearchDateOnlyReturnsAllInRangeAsFilenameOnly()
    {
        // Arrange — range 2025-01-10 .. 2025-01-12 => [2025-01-10 00:00, 2025-01-13 00:00).
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await ApplyCas038MigrationAsync(connection);
        var bucketName = "cas038-sc9-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            // Names deliberately share no common term — a date-only search must not depend on one.
            var fromBoundary = await SeedFileIndexRowAsync(connection, bucketName, "alpha.txt", "2025-01-10T00:00:00.000");
            var midRange = await SeedFileIndexRowAsync(connection, bucketName, "bravo.txt", "2025-01-11T12:00:00.000");
            var toBoundary = await SeedFileIndexRowAsync(connection, bucketName, "charlie.txt", "2025-01-12T23:59:59.999");
            var justBefore = await SeedFileIndexRowAsync(connection, bucketName, "delta.txt", "2025-01-09T23:59:59.999");
            var toPlusOne = await SeedFileIndexRowAsync(connection, bucketName, "echo.txt", "2025-01-13T00:00:00.000");
            var inactiveInRange = await SeedFileIndexRowAsync(connection, bucketName, "foxtrot.txt", "2025-01-11T08:00:00.000");

            var repository = new FileIndexRepository(BuildRepositoryConfiguration());
            await repository.SoftDeleteAsync(inactiveInRange, "test-fixture", CancellationToken.None);

            // Act
            var candidates = await repository.SearchCandidatesAsync(
                bucketName, "All", "", new DateOnly(2025, 1, 10), new DateOnly(2025, 1, 12), CancellationToken.None);

            // Assert — every in-range active row exactly once, all as Filename; zero Content rows.
            candidates.Select(c => c.FileIndexId).Should().BeEquivalentTo(new[] { fromBoundary, midRange, toBoundary });
            candidates.Should().OnlyContain(c => c.MatchType == "Filename");
            candidates.Where(c => c.MatchType == "Content").Should().BeEmpty();

            // Assert — out-of-range and inactive rows excluded.
            candidates.Select(c => c.FileIndexId).Should().NotContain(new[] { justBefore, toPlusOne, inactiveInRange });

            // Assert — empty term with no range is still rejected by the procedure's guard.
            var noTermNoRange = async () => await repository.SearchCandidatesAsync(
                bucketName, "All", "", null, null, CancellationToken.None);
            (await noTermNoRange.Should().ThrowAsync<SqlException>())
                .Which.Message.Should().Contain("@term is required.");
        }
        finally
        {
            await DeleteBucketRowsAsync(connection, bucketName);
        }
    }
    // =================================================================================================
    // CAS-040 §5/§13 "FileIndexRepositoryTests" — dbo.file_index_get contract
    // =================================================================================================

    private const string Cas040SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database with the " +
        "CAS-040 migration (20261001_090000__CAS-040-file-viewer-download.sql) applied — not " +
        "available in this sandbox; run manually against a real instance before merging.";

    [Fact(Skip = Cas040SkipReason)]
    public async Task Test_FileIndexGet_ReturnsActiveAndInactiveRows()
    {
        // Arrange — one active row and one row soft-deleted to is_active = 0 (FR-08/FR-13/FR-16).
        var repository = new FileIndexRepository(BuildRepositoryConfiguration());
        var bucketName = "cas040-get-" + Guid.NewGuid().ToString("N")[..8];
        var modifiedAt = new DateTimeOffset(2026, 8, 14, 10, 22, 5, TimeSpan.Zero);

        var activeId = await repository.InsertAsync(
            "test-fixture", "test-fixture", "ODrive", bucketName, "contracts/cas040/active.xlsx", "active.xlsx",
            "xlsx", 482113, modifiedAt, sha1Hash: null, lastSeenAt: null, CancellationToken.None);
        var inactiveId = await repository.InsertAsync(
            "test-fixture", "test-fixture", "B2", bucketName, "contracts/cas040/inactive.pdf", "inactive.pdf",
            "pdf", 1024, modifiedAt, sha1Hash: null, lastSeenAt: null, CancellationToken.None);
        await repository.SoftDeleteAsync(inactiveId, "test-fixture", CancellationToken.None);

        // Act
        var active = await repository.GetAsync(activeId, CancellationToken.None);
        var inactive = await repository.GetAsync(inactiveId, CancellationToken.None);
        var unknown = await repository.GetAsync(int.MaxValue, CancellationToken.None);

        // Assert — every §5 column round-trips; inactive rows are returned (the caller maps them to
        // 410); zero rows = not found (null).
        active.Should().NotBeNull();
        active!.FileIndexId.Should().Be(activeId);
        active.Source.Should().Be("ODrive");
        active.BucketName.Should().Be(bucketName);
        active.RelativePath.Should().Be("contracts/cas040/active.xlsx");
        active.FileName.Should().Be("active.xlsx");
        active.FileExtension.Should().Be("xlsx");
        active.FileSizeBytes.Should().Be(482113);
        active.ModifiedAt.Should().Be(modifiedAt);
        active.IsActive.Should().BeTrue();

        inactive.Should().NotBeNull();
        inactive!.Source.Should().Be("B2");
        inactive.IsActive.Should().BeFalse();

        unknown.Should().BeNull();
    }
}
