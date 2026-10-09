using System.Data;
using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-047 §13 real-DB tests for <c>dbo.search_result_filename_ins</c> via
/// <see cref="SearchResultRepository.InsertFilenameMatchesAsync"/> (SC-2, SC-3, SC-4 set-based half).
/// <see cref="RealDbFactAttribute"/>: runs whenever ConnectionStrings:CachetArchiveSync is configured,
/// reported Skipped otherwise. Applies the CAS-047 migration itself first (idempotent CREATE OR ALTER),
/// seeds rows in a unique bucket, and removes everything it created.
/// </summary>
[Collection("CAS-047 file_index real-DB")]
public sealed class SearchResultFilenameRepositoryTests : IAsyncLifetime
{
    private const string MigrationFileName = "20261004_220000__CAS-047-search-modes.sql";
    private readonly List<int> _taskIds = new();
    private readonly List<string> _buckets = new();
    private int _userId;

    private static string ConnectionString => RealDbConfiguration.ConnectionString!;

    private static IConfiguration RepoConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = ConnectionString })
            .Build();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Database", "migrations")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var script = (await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "Database", "migrations", MigrationFileName)))
            .Replace("USE [CachetArchiveSync];", $"USE [{connection.Database}];");
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                     .Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await connection.ExecuteAsync(batch);
        }

        _userId = await connection.QuerySingleAsync<int>(
            "INSERT INTO dbo.users (created_by, updated_by, username, password_hash, b2_application_key_id, b2_application_key) " +
            "OUTPUT inserted.user_id VALUES (N'test-fixture', N'test-fixture', @username, N'x', N'ct:keyid', N'ct:key')",
            new { username = "cas047-" + Guid.NewGuid().ToString("N")[..12] });
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        if (_taskIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM dbo.search_result WHERE task_queue_id IN @ids", new { ids = _taskIds });
        }

        foreach (var bucket in _buckets)
        {
            await connection.ExecuteAsync("DELETE FROM dbo.file_index WHERE bucket_name = @bucket", new { bucket });
        }

        if (_taskIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM dbo.task_queue WHERE task_queue_id IN @ids", new { ids = _taskIds });
        }

        await connection.ExecuteAsync("DELETE FROM dbo.users WHERE user_id = @id", new { id = _userId });
    }

    private async Task<int> SeedTaskAsync(SqlConnection connection)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", "KeywordSearch");
        parameters.Add("@submitted_by_user_id", _userId);
        parameters.Add("@request_parameters", (string?)null);
        parameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: CommandType.StoredProcedure));
        var id = parameters.Get<int>("@task_queue_id");
        _taskIds.Add(id);
        return id;
    }

    private Task<int> SeedFileAsync(string source, string bucket, string relativePath, string extension = "txt")
    {
        _buckets.Add(bucket);
        return new FileIndexRepository(RepoConfig()).InsertAsync(
            "test-fixture", "test-fixture", source, bucket, relativePath, relativePath.Split('/').Last(),
            extension, 1, DateTimeOffset.UtcNow, sha1Hash: null, lastSeenAt: null, ct: CancellationToken.None);
    }

    private sealed record StoredRow(int file_index_id, string match_type, string match_status);

    private static async Task<List<StoredRow>> StoredAsync(SqlConnection connection, int taskId) =>
        (await connection.QueryAsync<StoredRow>(
            "SELECT file_index_id, match_type, match_status FROM dbo.search_result WHERE task_queue_id = @taskId", new { taskId })).ToList();

    [RealDbFact]
    public async Task Test_SC2_FilenameMatchesB2FileNameOnly()
    {
        // Arrange
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var bucket = "cas047-sc2-" + Guid.NewGuid().ToString("N")[..8];
        var taskId = await SeedTaskAsync(connection);
        var b2Match = await SeedFileAsync("B2", bucket, "docs/my-agreement-final.txt");
        var odriveSameName = await SeedFileAsync("ODrive", bucket, "docs/my-agreement-final.txt");
        var folderOnly = await SeedFileAsync("B2", bucket, "agreement-folder/readme.txt");
        var inactive = await SeedFileAsync("B2", bucket, "docs/old-agreement.txt");
        await new FileIndexRepository(RepoConfig()).SoftDeleteAsync(inactive, "test-fixture", CancellationToken.None);
        var repository = new SearchResultRepository(RepoConfig());

        // Act
        var inserted = await repository.InsertFilenameMatchesAsync(taskId, bucket, "All", "agreement", "worker-svc", CancellationToken.None);

        // Assert - B2 file-name match only; O-Drive, path-only and inactive rows excluded.
        inserted.Should().Be(1);
        var stored = await StoredAsync(connection, taskId);
        stored.Should().ContainSingle().Which.Should().Be(new StoredRow(b2Match, "Filename", "Flagged"));
        stored.Select(s => s.file_index_id).Should().NotContain(new[] { odriveSameName, folderOnly, inactive });
    }

    [RealDbFact]
    public async Task Test_SC3_FilenameInputWildcardsAreLiteral()
    {
        // Arrange
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var bucket = "cas047-sc3-" + Guid.NewGuid().ToString("N")[..8];
        var taskId = await SeedTaskAsync(connection);
        var literal = await SeedFileAsync("B2", bucket, "x/report-100%_[final]-v2.txt");
        await SeedFileAsync("B2", bucket, "x/report-100-final.txt");     // would match if % were a wildcard
        await SeedFileAsync("B2", bucket, "x/report-100ab[final].txt");  // would match if _ were a wildcard
        await SeedFileAsync("B2", bucket, "x/report-100%f.txt");         // would match if [final] were a class
        var repository = new SearchResultRepository(RepoConfig());

        // Act
        var inserted = await repository.InsertFilenameMatchesAsync(taskId, bucket, "All", "100%_[final]", "worker-svc", CancellationToken.None);

        // Assert
        inserted.Should().Be(1);
        (await StoredAsync(connection, taskId)).Select(s => s.file_index_id).Should().Equal(literal);
    }

    [RealDbFact]
    public async Task Test_SC3_FilenameInputBackslashIsLiteral()
    {
        // Arrange
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var bucket = "cas047-sc3b-" + Guid.NewGuid().ToString("N")[..8];
        var taskId = await SeedTaskAsync(connection);
        var literal = await SeedFileAsync("B2", bucket, @"x/xa\by.txt");
        await SeedFileAsync("B2", bucket, "x/xab-y.txt");
        var repository = new SearchResultRepository(RepoConfig());

        // Act
        var inserted = await repository.InsertFilenameMatchesAsync(taskId, bucket, "All", @"a\b", "worker-svc", CancellationToken.None);

        // Assert
        inserted.Should().Be(1);
        (await StoredAsync(connection, taskId)).Select(s => s.file_index_id).Should().Equal(literal);
    }

    [RealDbFact]
    public async Task Test_SC4_FilenameModeNoB2NoMirrorSetBasedInsert()
    {
        // Arrange - extension filter honoured; a second call for the same task is idempotent (no duplicates).
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var bucket = "cas047-sc4-" + Guid.NewGuid().ToString("N")[..8];
        var taskId = await SeedTaskAsync(connection);
        await SeedFileAsync("B2", bucket, "a/invoice-1.pdf", "pdf");
        await SeedFileAsync("B2", bucket, "a/invoice-2.pdf", "pdf");
        await SeedFileAsync("B2", bucket, "a/invoice-3.txt", "txt");
        var repository = new SearchResultRepository(RepoConfig());

        // Act
        var first = await repository.InsertFilenameMatchesAsync(taskId, bucket, "pdf", "invoice", "worker-svc", CancellationToken.None);
        var second = await repository.InsertFilenameMatchesAsync(taskId, bucket, "pdf", "invoice", "worker-svc", CancellationToken.None);

        // Assert
        first.Should().Be(2);
        second.Should().Be(0);
        (await StoredAsync(connection, taskId)).Should().HaveCount(2).And.OnlyContain(s => s.match_type == "Filename" && s.match_status == "Flagged");
    }

    [RealDbFact]
    public async Task Test_SC4_FilenameModeNoB2NoMirrorSetBasedInsert_ShortOrBlankTermRejectedBySp()
    {
        // Arrange
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var taskId = await SeedTaskAsync(connection);
        var repository = new SearchResultRepository(RepoConfig());

        // Act
        var tooShort = async () => await repository.InsertFilenameMatchesAsync(taskId, "any-bucket", "All", " ab ", "worker-svc", CancellationToken.None);
        var blank = async () => await repository.InsertFilenameMatchesAsync(taskId, "any-bucket", "All", "   ", "worker-svc", CancellationToken.None);

        // Assert
        await tooShort.Should().ThrowAsync<SqlException>();
        await blank.Should().ThrowAsync<SqlException>();
    }
}
