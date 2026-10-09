using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB round-trip tests for <see cref="SearchResultRepository"/> /
/// <c>dbo.search_result_ins</c>/<c>_get</c>/<c>_lst</c> (CAS-006 §5/§13 Test Plan
/// "SearchResultRepositoryTests" — supports SC-3). Proves what a mocked
/// <see cref="Cachet.ArchiveSync.Core.Interfaces.ISearchResultRepository"/> cannot: the real
/// <c>UNIQUE (task_queue_id, file_index_id, match_type)</c> constraint and the real
/// <c>dbo.search_result_lst</c> pagination/filter behavior.
///
/// Skip-marked in this sandbox — same reason and same setup convention as
/// <see cref="AuditLogRepositoryTests"/> (no live SQL Server instance reachable here). Un-skip and
/// run manually against a real instance with the CAS-006 migration
/// (<c>Database/migrations/20260922_170000__CAS-006-keyword-search-api.sql</c>) deployed.
/// </summary>
[Collection("CAS-047 file_index real-DB")]
public sealed class SearchResultRepositoryTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database — not " +
        "available in this sandbox; run manually against a real instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;

    public SearchResultRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
        _seedUserId = int.TryParse(configuration["IntegrationTests:SeedUserId"], out var id) ? id : 1;
    }

    private IConfiguration BuildRepositoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CachetArchiveSync"] = _connectionString
            })
            .Build();

    private async Task<int> SeedTaskQueueRowAsync(SqlConnection connection)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "api-svc");
        parameters.Add("@updated_by", "api-svc");
        parameters.Add("@task_type", "KeywordSearch");
        parameters.Add("@submitted_by_user_id", _seedUserId);
        parameters.Add("@request_parameters", (string?)null);
        parameters.Add("@task_queue_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await connection.ExecuteAsync(new Dapper.CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: System.Data.CommandType.StoredProcedure));
        return parameters.Get<int>("@task_queue_id");
    }

    private async Task<int> SeedFileIndexRowAsync(SqlConnection connection, string relativePath)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@source", "B2");
        parameters.Add("@bucket_name", "cachet-archive-prod");
        parameters.Add("@relative_path", relativePath);
        parameters.Add("@file_name", relativePath.Split('/').Last());
        parameters.Add("@file_extension", "pdf");
        parameters.Add("@file_size_bytes", 1024L);
        parameters.Add("@modified_at", DateTime.UtcNow);
        parameters.Add("@file_index_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await connection.ExecuteAsync(new Dapper.CommandDefinition(
            "dbo.file_index_ins", parameters, commandType: System.Data.CommandType.StoredProcedure));
        return parameters.Get<int>("@file_index_id");
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_InsertAsync_InsertsRetrievableRow()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/test-{Guid.NewGuid():N}.pdf");
        var repository = new SearchResultRepository(BuildRepositoryConfiguration());

        // Act
        var searchResultId = await repository.InsertAsync(
            "worker-svc", "worker-svc", taskQueueId, fileIndexId, "Filename", null, "Flagged", CancellationToken.None);

        // Assert
        var row = await repository.GetAsync(searchResultId, CancellationToken.None);
        row.Should().NotBeNull();
        row!.TaskQueueId.Should().Be(taskQueueId);
        row.FileIndexId.Should().Be(fileIndexId);
        row.MatchType.Should().Be("Filename");
        row.MatchStatus.Should().Be("Flagged");
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_ListAsync_PaginatesByMatchType()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var filenameFileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/filename-{Guid.NewGuid():N}.pdf");
        var contentFileIndexId = await SeedFileIndexRowAsync(connection, $"notes/content-{Guid.NewGuid():N}.docx");
        var repository = new SearchResultRepository(BuildRepositoryConfiguration());
        await repository.InsertAsync("worker-svc", "worker-svc", taskQueueId, filenameFileIndexId, "Filename", null, "Flagged", CancellationToken.None);
        await repository.InsertAsync("worker-svc", "worker-svc", taskQueueId, contentFileIndexId, "Content", "...snippet...", "Needs Review", CancellationToken.None);

        // Act
        var (filenameItems, filenameTotal) = await repository.ListAsync(taskQueueId, "Filename", page: 1, pageSize: 50, CancellationToken.None);
        var (contentItems, contentTotal) = await repository.ListAsync(taskQueueId, "Content", page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        filenameTotal.Should().Be(1);
        filenameItems.Should().ContainSingle(i => i.FileIndexId == filenameFileIndexId);
        contentTotal.Should().Be(1);
        contentItems.Should().ContainSingle(i => i.FileIndexId == contentFileIndexId && i.MatchSnippet == "...snippet...");
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_UniqueConstraint_RejectsDuplicateTaskFileMatchType()
    {
        // Arrange — CAS-006 §5's UQ_search_result_task_file_matchtype: the same
        // (task_queue_id, file_index_id, match_type) combination cannot be inserted twice.
        await using var connection = new SqlConnection(_connectionString);
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/dup-{Guid.NewGuid():N}.pdf");
        var repository = new SearchResultRepository(BuildRepositoryConfiguration());
        await repository.InsertAsync("worker-svc", "worker-svc", taskQueueId, fileIndexId, "Filename", null, "Flagged", CancellationToken.None);

        // Act
        var act = async () => await repository.InsertAsync(
            "worker-svc", "worker-svc", taskQueueId, fileIndexId, "Filename", null, "Flagged", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqlException>();
    }

    public void Dispose()
    {
        // Real run: clean up any inserted search_result/task_queue/file_index test rows here.
    }
}
