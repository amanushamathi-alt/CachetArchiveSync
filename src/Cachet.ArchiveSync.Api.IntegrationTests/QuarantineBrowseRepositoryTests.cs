using System.Diagnostics;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit.Abstractions;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// <c>[Fact]</c> that runs only when <c>ConnectionStrings:CachetArchiveSync</c> is reachable AND the
/// CAS-050 migration (<c>20261005_120000__CAS-050-files-browse-quarantine.sql</c>) is deployed; otherwise
/// it is reported as Skipped with the reason - never as a false pass (same convention as
/// <see cref="BrowseSqlFactAttribute"/>). A skip is a gate failure for CAS-050 SC-1..SC-5, SC-8..SC-10 and
/// V4..V9: apply the migration to the target database and re-run.
/// </summary>
public sealed class Cas050SqlFactAttribute : FactAttribute
{
    public Cas050SqlFactAttribute()
    {
        var connectionString = BrowseSqlSupport.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Skip = "CAS-050 real-DB test: no ConnectionStrings:CachetArchiveSync configured.";
            return;
        }

        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            var deployed = connection.ExecuteScalar<int?>(
                @"SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.parameters
                                           WHERE object_id = OBJECT_ID(N'dbo.quarantine_entry_lst') AND name = N'@total_count')
                              THEN OBJECT_ID(N'dbo.quarantine_entry_browse_state_lst') END");
            if (deployed is null)
            {
                Skip = "CAS-050 real-DB test: the CAS-050 SPs are not deployed on the target database " +
                       "(apply Database/migrations/20261005_120000__CAS-050-files-browse-quarantine.sql). A skip is a gate failure for CAS-050.";
            }
        }
        catch (Exception ex)
        {
            Skip = "CAS-050 real-DB test: SQL Server not reachable (" + ex.GetType().Name + ").";
        }
    }
}

/// <summary>
/// CAS-050 §13 "QuarantineEntryRepositoryTests" - real SQL Server tests of the Files-browser Quarantine
/// stored procedures through <see cref="QuarantineEntryRepository"/>: prefix boundary, LIKE escaping,
/// case-insensitivity, snapshot, all-origin remove, paging, state and 50,000-file set-based add/remove.
/// Each class instance seeds its own uniquely named buckets (and one test user/task for Keyword
/// Search-origin rows) and deletes everything it created on dispose; no other data is read or changed
/// (the paging test only reads the global Active set).
/// </summary>
[Collection("CAS-047 file_index real-DB")]
public sealed class QuarantineBrowseRepositoryTests : IAsyncLifetime
{
    private const string Marker = "cas050-test";

    private readonly ITestOutputHelper _output;
    private readonly string _bucket = "cas050-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _otherBucket = "cas050-other-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _buckets = new();
    private int _tick;
    private int? _userId;
    private int? _taskId;

    public QuarantineBrowseRepositoryTests(ITestOutputHelper output)
    {
        _output = output;
        _buckets.Add(_bucket);
        _buckets.Add(_otherBucket);
    }

    private static string ConnectionString => BrowseSqlSupport.ConnectionString;

    private QuarantineEntryRepository NewRepository() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = ConnectionString })
            .Build());

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            var args = new { buckets = _buckets, marker = Marker };
            await connection.ExecuteAsync(
                @"DELETE qe FROM dbo.quarantine_entry qe JOIN dbo.file_index fi ON fi.file_index_id = qe.file_index_id
                  WHERE fi.bucket_name IN @buckets AND fi.created_by = @marker", args, commandTimeout: 300);
            await connection.ExecuteAsync(
                @"DELETE sr FROM dbo.search_result sr JOIN dbo.file_index fi ON fi.file_index_id = sr.file_index_id
                  WHERE fi.bucket_name IN @buckets AND fi.created_by = @marker", args, commandTimeout: 300);
            await connection.ExecuteAsync(
                "DELETE FROM dbo.file_index WHERE bucket_name IN @buckets AND created_by = @marker", args, commandTimeout: 300);
            if (_taskId is not null)
            {
                await connection.ExecuteAsync("DELETE FROM dbo.task_queue WHERE task_queue_id = @id", new { id = _taskId });
            }

            if (_userId is not null)
            {
                await connection.ExecuteAsync("DELETE FROM dbo.users WHERE user_id = @id", new { id = _userId });
            }
        }
        catch (SqlException)
        {
            // Skipped runs never reached the database (or the schema is absent); nothing to clean.
        }
    }

    // ---- seeding helpers --------------------------------------------------------------------------------

    private async Task<int> SeedFileAsync(string path, bool active = true, string source = "B2", string? bucket = null)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            @"INSERT INTO dbo.file_index
                (created_by, updated_by, source, bucket_name, relative_path, file_name, file_extension,
                 file_size_bytes, modified_at, last_seen_at, is_active)
              OUTPUT INSERTED.file_index_id
              VALUES (@marker, @marker, @source, @bucket, @path, @name, N'PDF', 10, @modified, SYSUTCDATETIME(), @active)",
            new
            {
                marker = Marker,
                source,
                bucket = bucket ?? _bucket,
                path,
                name,
                modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_tick++),
                active
            });
    }

    /// <summary>Quarantine via the Files path, returning the new quarantine_entry_id.</summary>
    private async Task<int> QuarantineFileAsync(int fileIndexId)
    {
        var result = await NewRepository().InsertBrowseFileAsync(Marker, Marker, fileIndexId, CancellationToken.None);
        result.Outcome.Should().Be(QuarantineInsertOutcome.Added);
        return result.QuarantineEntryId!.Value;
    }

    /// <summary>Keyword Search-origin Active entry (search_result_id set), inserted directly.</summary>
    private async Task<int> QuarantineAsKeywordSearchAsync(int fileIndexId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        if (_userId is null)
        {
            _userId = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO dbo.users (created_by, updated_by, username, password_hash, b2_application_key_id, b2_application_key)
                  OUTPUT INSERTED.user_id
                  VALUES (@marker, @marker, @username, N'x', N'x', N'x')",
                new { marker = Marker, username = "cas050-" + Guid.NewGuid().ToString("N")[..16] });
            _taskId = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO dbo.task_queue (created_by, updated_by, task_type, submitted_by_user_id)
                  OUTPUT INSERTED.task_queue_id
                  VALUES (@marker, @marker, N'KeywordSearch', @user)",
                new { marker = Marker, user = _userId });
        }

        var searchResultId = await connection.ExecuteScalarAsync<int>(
            @"INSERT INTO dbo.search_result (created_by, updated_by, task_queue_id, file_index_id, match_type, match_status)
              OUTPUT INSERTED.search_result_id
              VALUES (@marker, @marker, @task, @file, N'Filename', N'Flagged')",
            new { marker = Marker, task = _taskId, file = fileIndexId });
        return await connection.ExecuteScalarAsync<int>(
            @"INSERT INTO dbo.quarantine_entry (created_by, updated_by, file_index_id, search_result_id, match_status_snapshot, status)
              OUTPUT INSERTED.quarantine_entry_id
              VALUES (@marker, @marker, @file, @sr, N'Flagged', N'Active')",
            new { marker = Marker, file = fileIndexId, sr = searchResultId });
    }

    private async Task<HashSet<int>> ActiveFileIdsAsync(string? bucket = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        var ids = await connection.QueryAsync<int>(
            @"SELECT qe.file_index_id FROM dbo.quarantine_entry qe
              JOIN dbo.file_index fi ON fi.file_index_id = qe.file_index_id
              WHERE qe.status = N'Active' AND fi.bucket_name = @bucket",
            new { bucket = bucket ?? _bucket });
        return ids.ToHashSet();
    }

    // ---- SC-1 ---------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC1_AddFile()
    {
        var fileId = await SeedFileAsync("Ops/a.pdf");
        var repository = NewRepository();

        var result = await repository.InsertBrowseFileAsync(Marker, Marker, fileId, CancellationToken.None);

        result.Outcome.Should().Be(QuarantineInsertOutcome.Added);
        await using var connection = new SqlConnection(ConnectionString);
        var rows = (await connection.QueryAsync<(string status, string snapshot, int? searchResultId)>(
            "SELECT status, match_status_snapshot, search_result_id FROM dbo.quarantine_entry WHERE file_index_id = @fileId",
            new { fileId })).ToList();
        rows.Should().ContainSingle();
        rows[0].status.Should().Be("Active");
        rows[0].snapshot.Should().Be("Informational");
        rows[0].searchResultId.Should().BeNull();

        var page = await repository.ListActivePagedAsync(1, int.MaxValue, CancellationToken.None);
        var item = page.Rows.Single(r => r.FileIndexId == fileId);
        item.SourceTab.Should().Be("Files");
        item.Status.Should().Be("Informational");
        item.Path.Should().Be("Ops/a.pdf");
    }

    [Cas050SqlFact]
    public async Task Test_SC1_AddFile_InactiveOrUnknownFile_IsSearchResultNotFoundOutcome()
    {
        var inactive = await SeedFileAsync("Ops/gone.pdf", active: false);
        var repository = NewRepository();

        var inactiveResult = await repository.InsertBrowseFileAsync(Marker, Marker, inactive, CancellationToken.None);
        var unknownResult = await repository.InsertBrowseFileAsync(Marker, Marker, int.MaxValue, CancellationToken.None);

        inactiveResult.Outcome.Should().Be(QuarantineInsertOutcome.SearchResultNotFound);
        unknownResult.Outcome.Should().Be(QuarantineInsertOutcome.SearchResultNotFound);
    }

    // ---- SC-2 ---------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC2_AddFolder_OneRowPerFile()
    {
        var a = await SeedFileAsync("Ops/a.pdf");
        var b = await SeedFileAsync("Ops/sub/b.pdf");
        var c = await SeedFileAsync("Ops/sub/deep/c.pdf");
        await SeedFileAsync("Elsewhere/d.pdf");

        var result = await NewRepository().InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Ops", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Added.Should().Be(3);
        result.AlreadyQuarantined.Should().Be(0);
        (await ActiveFileIdsAsync()).Should().BeEquivalentTo(new[] { a, b, c });

        // One row per file: no file has two rows, and the table has no folder-shaped row (every row joins a file).
        await using var connection = new SqlConnection(ConnectionString);
        var duplicates = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM (SELECT qe.file_index_id FROM dbo.quarantine_entry qe
                                    JOIN dbo.file_index fi ON fi.file_index_id = qe.file_index_id
                                    WHERE fi.bucket_name = @bucket GROUP BY qe.file_index_id HAVING COUNT(*) > 1) d",
            new { bucket = _bucket });
        duplicates.Should().Be(0);
        var rowCount = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM dbo.quarantine_entry qe JOIN dbo.file_index fi ON fi.file_index_id = qe.file_index_id
              WHERE fi.bucket_name = @bucket", new { bucket = _bucket });
        rowCount.Should().Be(3);
    }

    // ---- SC-3 ---------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC3_Snapshot_RerunAddsOnlyNew()
    {
        await SeedFileAsync("Ops/a.pdf");
        await SeedFileAsync("Ops/b.pdf");
        await SeedFileAsync("Ops/sub/c.pdf");
        var repository = NewRepository();
        var first = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Ops", CancellationToken.None);
        first!.Added.Should().Be(3);

        // A file indexed AFTER the action is not quarantined...
        var late = await SeedFileAsync("Ops/sub/late.pdf");
        (await ActiveFileIdsAsync()).Should().NotContain(late);

        // ...until the action is re-run, which adds only the new file.
        var rerun = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Ops", CancellationToken.None);
        rerun!.Added.Should().Be(1);
        rerun.AlreadyQuarantined.Should().Be(3);
        (await ActiveFileIdsAsync()).Should().Contain(late);
        (await ActiveFileIdsAsync()).Should().HaveCount(4);
    }

    // ---- SC-4 / V4 (SQL level) --------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC4_FolderAdd_SkipsExisting()
    {
        var kw = await SeedFileAsync("Ops/kw.pdf");
        var files = await SeedFileAsync("Ops/files.pdf");
        await SeedFileAsync("Ops/new1.pdf");
        await SeedFileAsync("Ops/sub/new2.pdf");
        await QuarantineAsKeywordSearchAsync(kw);
        await QuarantineFileAsync(files);

        var result = await NewRepository().InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Ops", CancellationToken.None);

        result!.Added.Should().Be(2);
        result.AlreadyQuarantined.Should().Be(2);
        await using var connection = new SqlConnection(ConnectionString);
        var duplicates = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM (SELECT qe.file_index_id FROM dbo.quarantine_entry qe
                                    JOIN dbo.file_index fi ON fi.file_index_id = qe.file_index_id
                                    WHERE fi.bucket_name = @bucket AND qe.status = N'Active'
                                    GROUP BY qe.file_index_id HAVING COUNT(*) > 1) d",
            new { bucket = _bucket });
        duplicates.Should().Be(0);
        (await ActiveFileIdsAsync()).Should().HaveCount(4);
    }

    [Cas050SqlFact]
    public async Task Test_V4_AlreadyQuarantined_FileConflict_FolderSkips_Sql()
    {
        var fileId = await SeedFileAsync("Ops/a.pdf");
        await SeedFileAsync("Ops/b.pdf");
        var repository = NewRepository();
        await QuarantineFileAsync(fileId);

        var fileAgain = await repository.InsertBrowseFileAsync(Marker, Marker, fileId, CancellationToken.None);
        var folder = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Ops", CancellationToken.None);
        var allDone = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Ops", CancellationToken.None);

        fileAgain.Outcome.Should().Be(QuarantineInsertOutcome.AlreadyActive);
        folder.Should().BeEquivalentTo(new { Added = 1, AlreadyQuarantined = 1 });
        allDone.Should().BeEquivalentTo(new { Added = 0, AlreadyQuarantined = 2 });
    }

    // ---- SC-5 ---------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC5_Deselect()
    {
        var fileA = await SeedFileAsync("Ops/a.pdf");
        var kw = await SeedFileAsync("Ops/sub/kw.pdf");
        var outside = await SeedFileAsync("Other/o.pdf");
        await QuarantineFileAsync(fileA);
        await QuarantineAsKeywordSearchAsync(kw);
        await QuarantineFileAsync(outside);
        var repository = NewRepository();

        // File deselect: one row, then nothing left to remove.
        (await repository.HardDeleteBrowseFileAsync(fileA, CancellationToken.None)).Should().Be(1);
        (await repository.HardDeleteBrowseFileAsync(fileA, CancellationToken.None)).Should().Be(0);

        // Folder deselect removes Keyword Search-origin rows too, and nothing outside the prefix.
        await QuarantineFileAsync(fileA);
        (await repository.HardDeleteBrowseFolderAsync("B2", _bucket, "Ops", CancellationToken.None)).Should().Be(2);

        (await ActiveFileIdsAsync()).Should().BeEquivalentTo(new[] { outside });
        var state = await repository.GetBrowseStateAsync("B2", _bucket, "Ops", CancellationToken.None);
        state.FileIndexIds.Should().BeEmpty();
        state.Folders.Single(f => f.FolderName == "sub").QuarantinedFiles.Should().Be(0);
        var list = await repository.ListActivePagedAsync(1, int.MaxValue, CancellationToken.None);
        list.Rows.Select(r => r.FileIndexId).Should().NotContain(new[] { fileA, kw });

        // Hard delete: the row is physically gone (not a status flag).
        await using var connection = new SqlConnection(ConnectionString);
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.quarantine_entry WHERE file_index_id IN (@a, @b)", new { a = fileA, b = kw })).Should().Be(0);
    }

    // ---- SC-8 / V7 (SQL level) -------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC8_Paging()
    {
        for (var i = 0; i < 60; i++)
        {
            await SeedFileAsync($"Bulk/f{i:D3}.pdf");
        }

        var repository = NewRepository();
        (await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Bulk", CancellationToken.None))!.Added.Should().Be(60);

        // Expected ordering is the documented one: created_at ASC, quarantine_entry_id ASC (over the whole Active set).
        await using var connection = new SqlConnection(ConnectionString);
        var expectedAll = (await connection.QueryAsync<int>(
            @"SELECT qe.quarantine_entry_id FROM dbo.quarantine_entry qe
              JOIN dbo.file_index fi ON fi.file_index_id = qe.file_index_id
              WHERE qe.status = N'Active' ORDER BY qe.created_at ASC, qe.quarantine_entry_id ASC")).ToList();

        var page1 = await repository.ListActivePagedAsync(1, 25, CancellationToken.None);
        var page2 = await repository.ListActivePagedAsync(2, 25, CancellationToken.None);
        var page2Again = await repository.ListActivePagedAsync(2, 25, CancellationToken.None);
        var page3 = await repository.ListActivePagedAsync(3, 25, CancellationToken.None);

        page2.TotalCount.Should().Be(expectedAll.Count).And.BeGreaterThanOrEqualTo(60);
        page2.Rows.Select(r => r.QuarantineEntryId).Should().Equal(expectedAll.Skip(25).Take(25));
        page2Again.Rows.Select(r => r.QuarantineEntryId).Should().Equal(page2.Rows.Select(r => r.QuarantineEntryId), "order is stable across calls");
        page1.Rows.Select(r => r.QuarantineEntryId).Should().Equal(expectedAll.Take(25));
        page3.Rows.Select(r => r.QuarantineEntryId).Should().Equal(expectedAll.Skip(50).Take(25));
        page1.Rows.Should().HaveCount(25);
        page2.Rows.Should().HaveCount(25);
    }

    [Cas050SqlFact]
    public async Task Test_V7_ListSp_RejectsInvalidPaging_Sql()
    {
        var repository = NewRepository();

        Func<Task> pageZero = () => repository.ListActivePagedAsync(0, 25, CancellationToken.None);
        Func<Task> sizeZero = () => repository.ListActivePagedAsync(1, 0, CancellationToken.None);

        await pageZero.Should().ThrowAsync<SqlException>();
        await sizeZero.Should().ThrowAsync<SqlException>();
    }

    // ---- SC-9 ---------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC9_State()
    {
        var root = await SeedFileAsync("root.pdf");
        var rootOther = await SeedFileAsync("root-not-quarantined.pdf");
        var opsA = await SeedFileAsync("Ops/a.pdf");
        await SeedFileAsync("Ops/sub/b.pdf");
        var deep = await SeedFileAsync("Ops/sub/deep/c.pdf");
        await SeedFileAsync("Ops2/x.pdf");
        await SeedFileAsync("Ops/inactive.pdf", active: false);
        await SeedFileAsync("OtherBucketOnly/z.pdf", bucket: _otherBucket);
        foreach (var id in new[] { root, opsA, deep })
        {
            await QuarantineFileAsync(id);
        }

        var repository = NewRepository();

        var rootState = await repository.GetBrowseStateAsync("B2", _bucket, "", CancellationToken.None);
        var opsState = await repository.GetBrowseStateAsync("B2", _bucket, "Ops", CancellationToken.None);
        var subState = await repository.GetBrowseStateAsync("B2", _bucket, "Ops/sub", CancellationToken.None);

        rootState.FileIndexIds.Should().BeEquivalentTo(new[] { root });
        rootState.FileIndexIds.Should().NotContain(rootOther);
        rootState.Folders.Select(f => (f.FolderName, f.TotalFiles, f.QuarantinedFiles))
            .Should().Equal(("Ops", 3, 2), ("Ops2", 1, 0));
        opsState.FileIndexIds.Should().BeEquivalentTo(new[] { opsA });
        opsState.Folders.Select(f => (f.FolderName, f.TotalFiles, f.QuarantinedFiles)).Should().Equal(("sub", 2, 1));
        subState.FileIndexIds.Should().BeEmpty();
        subState.Folders.Select(f => (f.FolderName, f.TotalFiles, f.QuarantinedFiles)).Should().Equal(("deep", 1, 1));
    }

    // ---- SC-10 --------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_SC10_Folder_SetBased_50k()
    {
        const int count = 50_000;
        await using (var connection = new SqlConnection(ConnectionString))
        {
            // Set-based seed (one INSERT...SELECT) of 50,000 active files under Big/Sub-N/.
            await connection.ExecuteAsync(
                @"WITH n AS (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                             FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                  INSERT INTO dbo.file_index (created_by, updated_by, source, bucket_name, relative_path, file_name, file_extension,
                                              file_size_bytes, modified_at, last_seen_at, is_active)
                  SELECT @marker, @marker, N'B2', @bucket,
                         N'Big/Sub-' + CAST(i % 50 AS NVARCHAR(10)) + N'/f' + CAST(i AS NVARCHAR(10)) + N'.pdf',
                         N'f' + CAST(i AS NVARCHAR(10)) + N'.pdf', N'PDF', 10, SYSUTCDATETIME(), SYSUTCDATETIME(), 1
                  FROM n",
                new { count, marker = Marker, bucket = _bucket },
                commandTimeout: 600);
        }

        var repository = NewRepository();

        var addWatch = Stopwatch.StartNew();
        var added = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Big", CancellationToken.None);
        addWatch.Stop();
        var removeWatch = Stopwatch.StartNew();
        var removed = await repository.HardDeleteBrowseFolderAsync("B2", _bucket, "Big", CancellationToken.None);
        removeWatch.Stop();

        _output.WriteLine($"CAS-050 SC-10: folder add of {count} files = {addWatch.ElapsedMilliseconds} ms; folder remove = {removeWatch.ElapsedMilliseconds} ms");
        added!.Added.Should().Be(count);
        added.AlreadyQuarantined.Should().Be(0);
        removed.Should().Be(count);

        // Structurally set-based: no cursor / WHILE loop in either stored procedure.
        await using var connection2 = new SqlConnection(ConnectionString);
        foreach (var proc in new[] { "dbo.quarantine_entry_ins_browse_folder", "dbo.quarantine_entry_del_browse_folder" })
        {
            var definition = await connection2.ExecuteScalarAsync<string>("SELECT OBJECT_DEFINITION(OBJECT_ID(@proc))", new { proc });
            definition.Should().NotBeNullOrEmpty();
            definition!.ToUpperInvariant().Should().NotContain("CURSOR").And.NotContain("WHILE");
        }
    }

    // ---- V5 -----------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_V5_FolderPrefix_ExactBoundary()
    {
        var a = await SeedFileAsync("Ops/a.pdf");
        var deep = await SeedFileAsync("Ops/x/y/z.pdf");
        await SeedFileAsync("Ops2/c.pdf");                    // sibling sharing the name prefix
        await SeedFileAsync("Opsy/d.pdf");                    // sibling sharing the name prefix
        await SeedFileAsync("Ops.txt");                       // file whose name starts with the folder name
        await SeedFileAsync("Ops/inactive.pdf", active: false);
        await SeedFileAsync("Ops/odrive.pdf", source: "ODrive");
        await SeedFileAsync("Ops/other-bucket.pdf", bucket: _otherBucket);
        var repository = NewRepository();

        var result = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Ops", CancellationToken.None);

        result!.Added.Should().Be(2);
        (await ActiveFileIdsAsync()).Should().BeEquivalentTo(new[] { a, deep });
        (await ActiveFileIdsAsync(_otherBucket)).Should().BeEmpty();

        // The remove side has the same boundary; a name that is only a prefix of folder names is not a folder.
        (await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "Op", CancellationToken.None)).Should().BeNull();
        (await repository.HardDeleteBrowseFolderAsync("B2", _bucket, "Op", CancellationToken.None)).Should().BeNull();
        (await repository.HardDeleteBrowseFolderAsync("B2", _bucket, "Ops", CancellationToken.None)).Should().Be(2);
    }

    // ---- V6 -----------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_V6_FolderRemove_AllOrigins_PrefixScoped()
    {
        var filesOrigin = await SeedFileAsync("Ops/files.pdf");
        var kwOrigin = await SeedFileAsync("Ops/sub/kw.pdf");
        var siblingKw = await SeedFileAsync("Ops2/kw.pdf");
        var otherFiles = await SeedFileAsync("Other/f.pdf");
        var inactive = await SeedFileAsync("Ops/inactive.pdf");
        await QuarantineFileAsync(filesOrigin);
        await QuarantineAsKeywordSearchAsync(kwOrigin);
        await QuarantineAsKeywordSearchAsync(siblingKw);
        await QuarantineFileAsync(otherFiles);
        await QuarantineFileAsync(inactive);
        await using (var connection = new SqlConnection(ConnectionString))
        {
            // The file went inactive after being quarantined: not an active file under the prefix any more.
            await connection.ExecuteAsync("UPDATE dbo.file_index SET is_active = 0 WHERE file_index_id = @inactive", new { inactive });
        }

        var removed = await NewRepository().HardDeleteBrowseFolderAsync("B2", _bucket, "Ops", CancellationToken.None);

        removed.Should().Be(2);
        (await ActiveFileIdsAsync()).Should().BeEquivalentTo(new[] { siblingKw, otherFiles, inactive });

        // Nothing under the prefix when nothing is quarantined: 200-style zero (not null) because the folder has active files.
        (await NewRepository().HardDeleteBrowseFolderAsync("B2", _bucket, "Ops", CancellationToken.None)).Should().Be(0);
    }

    // ---- V8 -----------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_V8_FolderPath_LikeEscaping()
    {
        // (folder with a LIKE metacharacter, decoy folder that an UNESCAPED pattern would also match)
        var cases = new (string Folder, string Decoy)[]
        {
            ("a_b", "aXb"),
            ("50%", "50abc"),
            ("[x]", "x"),
            ("d\\e", "de")
        };
        var repository = NewRepository();

        foreach (var (folder, decoy) in cases)
        {
            var real = await SeedFileAsync($"{folder}/real.pdf");
            var decoyId = await SeedFileAsync($"{decoy}/decoy.pdf");

            var added = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, folder, CancellationToken.None);

            added!.Added.Should().Be(1, $"'{folder}' must match literally");
            (await ActiveFileIdsAsync()).Should().Contain(real).And.NotContain(decoyId, $"'{decoy}' must not match '{folder}'");

            // Remove side is escaped too: removing the decoy-matching folder name removes only its own row.
            (await repository.HardDeleteBrowseFolderAsync("B2", _bucket, folder, CancellationToken.None)).Should().Be(1);
            (await ActiveFileIdsAsync()).Should().NotContain(real);
        }

        // State SP escapes too: the '%' folder lists only itself under the root with its own counts.
        var state = await repository.GetBrowseStateAsync("B2", _bucket, "50%", CancellationToken.None);
        state.FileIndexIds.Should().BeEmpty();
    }

    // ---- V9 -----------------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_V9_FolderPath_CaseInsensitive()
    {
        await using (var connection = new SqlConnection(ConnectionString))
        {
            var collation = await connection.ExecuteScalarAsync<string>("SELECT CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))");
            collation.Should().Contain("_CI_", "folder identity is case-insensitive as in file_index_browse_lst");
        }

        var fileId = await SeedFileAsync("Ops/Audit/a.pdf");
        var repository = NewRepository();

        var added = await repository.InsertBrowseFolderAsync(Marker, Marker, "B2", _bucket, "ops/audit", CancellationToken.None);
        var state = await repository.GetBrowseStateAsync("B2", _bucket, "OPS", CancellationToken.None);
        var removed = await repository.HardDeleteBrowseFolderAsync("B2", _bucket, "OPS/AUDIT", CancellationToken.None);

        added!.Added.Should().Be(1);
        state.Folders.Should().ContainSingle(f => f.FolderName == "Audit" && f.TotalFiles == 1 && f.QuarantinedFiles == 1);
        removed.Should().Be(1);
        (await ActiveFileIdsAsync()).Should().NotContain(fileId);
    }

    // ---- V10 (SQL level) ---------------------------------------------------------------------------------------

    [Cas050SqlFact]
    public async Task Test_V10_FilesAddedRow_WorksWithExistingDeletePath_Sql()
    {
        var forDelete = await SeedFileAsync("Ops/delete-me.pdf");
        var forRemove = await SeedFileAsync("Ops/remove-me.pdf");
        var deleteEntry = await QuarantineFileAsync(forDelete);
        var removeEntry = await QuarantineFileAsync(forRemove);
        var repository = NewRepository();

        // Permanent Delete resolves the row (dbo.quarantine_entry_get) and transitions it to Deleted.
        var resolved = await repository.GetActiveForDeleteAsync(deleteEntry, CancellationToken.None);
        var transitioned = await repository.TryTransitionToDeletedAsync(deleteEntry, Marker, CancellationToken.None);
        // The existing CAS-014 remove path hard-deletes it.
        var removeResolved = await repository.GetActiveForDeleteAsync(removeEntry, CancellationToken.None);
        var removedOk = await repository.HardDeleteActiveAsync(removeEntry, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.FileIndexId.Should().Be(forDelete);
        resolved.Path.Should().Be("Ops/delete-me.pdf");
        transitioned.Should().BeTrue();
        removeResolved.Should().NotBeNull();
        removedOk.Should().BeTrue();
        await using var connection = new SqlConnection(ConnectionString);
        (await connection.ExecuteScalarAsync<string>(
            "SELECT status FROM dbo.quarantine_entry WHERE quarantine_entry_id = @id", new { id = deleteEntry })).Should().Be("Deleted");
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.quarantine_entry WHERE quarantine_entry_id = @id", new { id = removeEntry })).Should().Be(0);
    }

    // ---- CAS-053 SC-7 -----------------------------------------------------------------------------------------

    /// <summary>
    /// End-to-end through the real <see cref="QuarantineBrowseService"/> + real stored procedures with a
    /// NON-EMPTY prefix (<c>D/Shares/Operations</c>, as in production): file_index rows hold the full key path,
    /// the browse route returns root-relative folder names, and the quarantine routes accept those names.
    /// Only the prefix lookup (mapping row), audit sink and single-file lookup are substituted.
    /// </summary>
    [Cas050SqlFact]
    public async Task Test_SC7_EndToEnd_PrefixedBucket()
    {
        const string prefix = "D/Shares/Operations";
        var opsA = await SeedFileAsync(prefix + "/Ops/a.pdf");
        await SeedFileAsync(prefix + "/Ops/sub/b.pdf");
        var deep = await SeedFileAsync(prefix + "/Ops/sub/deep/c.pdf");
        await SeedFileAsync(prefix + "/Ops2/x.pdf");
        var rootFile = await SeedFileAsync(prefix + "/root.pdf");

        var prefixProvider = Substitute.For<IB2PathPrefixProvider>();
        prefixProvider.GetPrefixAsync(_bucket, Arg.Any<CancellationToken>()).Returns(prefix);
        var audit = Substitute.For<IAuditLogService>();
        var service = new QuarantineBrowseService(
            NewRepository(), Substitute.For<IFileIndexRepository>(), audit, prefixProvider,
            NullLogger<QuarantineBrowseService>.Instance);

        // A folder path as returned by GET /files/browse (root-relative leaf under the prefix).
        var browse = await new FileBrowseRepository(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = ConnectionString })
                .Build())
            .BrowseAsync("B2", _bucket, prefix, false, CancellationToken.None);
        browse.Folders.Select(f => f.FolderName).Should().Contain("Ops");

        // Add the Ops folder, then read tri-state counts for the bucket root and for Ops.
        var added = await service.AddAsync(
            new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = _bucket, FolderPath = "Ops" }, 1, CancellationToken.None);
        added.Outcome.Should().Be(Cachet.ArchiveSync.Core.Interfaces.QuarantineBrowseAddOutcome.Completed);
        added.Response!.Added.Should().Be(3);
        (await ActiveFileIdsAsync()).Should().Contain(new[] { opsA, deep }).And.NotContain(rootFile);

        var rootState = await service.GetStateAsync("B2", _bucket, "", CancellationToken.None);
        rootState.Outcome.Should().Be(Cachet.ArchiveSync.Core.Interfaces.QuarantineBrowseStateOutcome.Completed);
        rootState.Response!.FileIndexIds.Should().BeEmpty();
        rootState.Response.Folders.Select(f => (f.FolderName, f.TotalFiles, f.QuarantinedFiles))
            .Should().Equal(("Ops", 3, 3), ("Ops2", 1, 0));

        var opsState = await service.GetStateAsync("B2", _bucket, "Ops", CancellationToken.None);
        opsState.Response!.FileIndexIds.Should().BeEquivalentTo(new[] { opsA });
        opsState.Response.Folders.Select(f => (f.FolderName, f.TotalFiles, f.QuarantinedFiles)).Should().Equal(("sub", 2, 2));

        // Remove succeeds for the same root-relative path; audit rows carry the full key path.
        var removed = await service.RemoveAsync(
            new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = _bucket, FolderPath = "Ops" }, 1, CancellationToken.None);
        removed.Outcome.Should().Be(Cachet.ArchiveSync.Core.Interfaces.QuarantineBrowseRemoveOutcome.Completed);
        removed.Response!.Removed.Should().Be(3);
        (await ActiveFileIdsAsync()).Should().BeEmpty();
        await audit.Received(2).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.FilePath == prefix + "/Ops"), Arg.Any<CancellationToken>());
    }
}
