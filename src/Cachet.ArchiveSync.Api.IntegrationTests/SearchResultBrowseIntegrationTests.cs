using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit.Abstractions;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// The real <c>Program.cs</c> pipeline wired to the REAL SQL Server repositories (task queue, search
/// results, browse, quarantine) for CAS-049's seeded-DB tests. Only the session/credential surface, the
/// audit writer (so 1,000-file quarantine runs do not pollute <c>dbo.audit_log</c>) and the bucket-prefix
/// provider are substituted. The cookie/policy/middleware path is the production one.
/// </summary>
internal sealed class SearchBrowseDbWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";
    public const string Prefix = "D/Shares/Operations";

    private readonly string _connectionString;
    private readonly string _bucket;
    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public IB2PathPrefixProvider PrefixProvider { get; } = Substitute.For<IB2PathPrefixProvider>();

    public SearchBrowseDbWebApplicationFactory(string connectionString, string bucket)
    {
        _connectionString = connectionString;
        _bucket = bucket;

        // CAS-020 §9: Program.cs fails fast without Admin:ProvisioningKey, and only an environment variable is
        // visible at that point (same idiom as SyncApiWebApplicationFactory). Test-only value, never a real secret.
        Environment.SetEnvironmentVariable("Admin__ProvisioningKey", "test-admin-provisioning-key-value");

        PrefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Prefix);
        UserRepository
            .GetSessionTokenVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<int?>(_sessionTokenVersions.TryGetValue(call.Arg<int>(), out var v) ? v : null));
        UserRepository
            .IncrementSessionTokenVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var next = (_sessionTokenVersions.TryGetValue(call.Arg<int>(), out var v) ? v : 0) + 1;
                _sessionTokenVersions[call.Arg<int>()] = next;
                return Task.FromResult(next);
            });
    }

    public void RegisterSession(int userId) => _sessionTokenVersions.TryAdd(userId, 0);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:DataProtection:KeyRingPath"] = _keyRingPath,
            ["ConnectionStrings:CachetArchiveSync"] = _connectionString,
            ["B2:BucketName"] = _bucket,
            ["Search:MaxTermLength"] = "200"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserRepository>();
            services.AddSingleton(UserRepository);
            services.RemoveAll<ICredentialProtector>();
            services.AddSingleton(CredentialProtector);
            services.RemoveAll<IUserB2KeyAuthorizer>();
            services.AddSingleton(B2KeyAuthorizer);
            services.RemoveAll<IAuditLogService>();
            services.AddSingleton(AuditLogService);
            services.RemoveAll<IB2PathPrefixProvider>();
            services.AddSingleton(PrefixProvider);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(_keyRingPath))
                {
                    Directory.Delete(_keyRingPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a per-instance temp directory.
            }
        }
    }
}

/// <summary>
/// CAS-049 §13 seeded-DB tests of <c>GET /search/{taskId}/browse</c> and <c>.../browse/result-ids</c> over
/// real HTTP (real Program.cs) and real SQL Server stored procedures. Uses <see cref="RealDbFactAttribute"/>
/// (the repo's convention): runs when <c>ConnectionStrings:CachetArchiveSync</c> is configured
/// (<c>appsettings.IntegrationTests.json</c> or environment), reported Skipped otherwise. Applies the CAS-049
/// migration itself first (idempotent <c>CREATE OR ALTER</c>), seeds rows in a unique bucket and a unique
/// user, and removes everything it created (quarantine entries, results, file index rows, tasks, users).
/// </summary>
[Collection("CAS-047 file_index real-DB")]
public sealed class SearchResultBrowseIntegrationTests : IAsyncLifetime
{
    private const string MigrationFileName = "20261004_230000__CAS-049-search-result-browse.sql";
    private const string Password = "correct horse battery staple";
    private const string ErrorBase = "https://cachet.tracecounts.internal/errors/";
    private const string Term = "zz-secret-term-zz";
    private static readonly PasswordHasher<object> Hasher = new();

    private readonly ITestOutputHelper _output;
    private readonly string _bucket = "cas049-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<int> _taskIds = new();
    private readonly List<int> _userIds = new();
    private SearchBrowseDbWebApplicationFactory? _factory;
    private HttpClient? _client;
    private int _userId;
    private int _otherUserId;
    private string _username = string.Empty;
    private string _otherUsername = string.Empty;
    private int _tick;

    public SearchResultBrowseIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string ConnectionString => RealDbConfiguration.ConnectionString!;

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

        (_userId, _username) = await SeedUserAsync(connection);
        (_otherUserId, _otherUsername) = await SeedUserAsync(connection);

        _factory = new SearchBrowseDbWebApplicationFactory(ConnectionString, _bucket);
        foreach (var (id, name) in new[] { (_userId, _username), (_otherUserId, _otherUsername) })
        {
            _factory.RegisterSession(id);
            _factory.UserRepository.GetByUsernameAsync(name, Arg.Any<CancellationToken>()).Returns(new UserAccount
            {
                UserId = id,
                Username = name,
                PasswordHash = Hasher.HashPassword(new object(), Password),
                B2ApplicationKeyId = "ct:keyid",
                B2ApplicationKey = "ct:key",
                IsActive = true,
                SessionTokenVersion = 0
            });
        }

        _factory.CredentialProtector.Unprotect("ct:keyid").Returns("plain-key-id");
        _factory.CredentialProtector.Unprotect("ct:key").Returns("plain-app-key");
        _factory.B2KeyAuthorizer
            .AuthorizeAsync("plain-key-id", "plain-app-key", Arg.Any<CancellationToken>())
            .Returns(new B2AuthorizationInfo
            {
                AccountId = "acct-1",
                AuthorizationToken = "4_secretB2token_neverLog",
                ApiUrl = "https://api-fake.backblazeb2.com",
                DownloadUrl = "https://download-fake.backblazeb2.com",
                // CAS-050 §2.8: POST /search/results/quarantine now requires Full Access.
                Capabilities = new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        _client.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            @"DELETE q FROM dbo.quarantine_entry q JOIN dbo.file_index fi ON fi.file_index_id = q.file_index_id
              WHERE fi.bucket_name = @bucket", new { bucket = _bucket }, commandTimeout: 300);
        if (_taskIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM dbo.search_result WHERE task_queue_id IN @ids", new { ids = _taskIds }, commandTimeout: 300);
        }

        await connection.ExecuteAsync("DELETE FROM dbo.file_index WHERE bucket_name = @bucket", new { bucket = _bucket }, commandTimeout: 300);
        if (_taskIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM dbo.task_queue WHERE task_queue_id IN @ids", new { ids = _taskIds });
        }

        if (_userIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM dbo.users WHERE user_id IN @ids", new { ids = _userIds });
        }
    }

    // ---- seeding ---------------------------------------------------------------------------------------

    private async Task<(int Id, string Username)> SeedUserAsync(SqlConnection connection)
    {
        var username = "cas049-" + Guid.NewGuid().ToString("N")[..12];
        var id = await connection.QuerySingleAsync<int>(
            "INSERT INTO dbo.users (created_by, updated_by, username, password_hash, b2_application_key_id, b2_application_key) " +
            "OUTPUT inserted.user_id VALUES (N'cas049-test', N'cas049-test', @username, N'x', N'ct:keyid', N'ct:key')",
            new { username });
        _userIds.Add(id);
        return (id, username);
    }

    private async Task<int> SeedTaskAsync(int? userId = null, string type = "KeywordSearch", string status = "Completed")
    {
        await using var connection = new SqlConnection(ConnectionString);
        var id = await connection.QuerySingleAsync<int>(
            @"INSERT INTO dbo.task_queue (created_by, updated_by, task_type, submitted_by_user_id, status, request_parameters, completed_at)
              OUTPUT inserted.task_queue_id
              VALUES (N'cas049-test', N'cas049-test', @type, @userId, @status, @parameters,
                      CASE WHEN @status = N'Completed' THEN SYSUTCDATETIME() END)",
            new { type, userId = userId ?? _userId, status, parameters = JsonSerializer.Serialize(new { term = Term, bucket = _bucket }) });
        _taskIds.Add(id);
        return id;
    }

    /// <summary>Seeds one active B2 file under the Operations prefix and (unless <paramref name="taskId"/> is null) a match row.</summary>
    private async Task<(int FileIndexId, int SearchResultId)> SeedMatchAsync(
        int? taskId, string pathUnderOperations, string matchType = "Filename", string status = "Flagged",
        bool active = true, string source = "B2", int? existingFileIndexId = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        var fileIndexId = existingFileIndexId ?? await connection.QuerySingleAsync<int>(
            @"INSERT INTO dbo.file_index
                (created_by, updated_by, source, bucket_name, relative_path, file_name, file_extension,
                 file_size_bytes, modified_at, last_seen_at, is_active)
              OUTPUT inserted.file_index_id
              VALUES (N'cas049-test', N'cas049-test', @source, @bucket, @path, @name, @ext, 10, @modified, SYSUTCDATETIME(), @active)",
            new
            {
                source,
                bucket = _bucket,
                path = SearchBrowseDbWebApplicationFactory.Prefix + "/" + pathUnderOperations,
                name = pathUnderOperations[(pathUnderOperations.LastIndexOf('/') + 1)..],
                ext = pathUnderOperations.Contains('.') ? pathUnderOperations[(pathUnderOperations.LastIndexOf('.') + 1)..] : null,
                modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(_tick++),
                active
            });
        var searchResultId = 0;
        if (taskId is not null)
        {
            searchResultId = await connection.QuerySingleAsync<int>(
                @"INSERT INTO dbo.search_result (created_by, updated_by, task_queue_id, file_index_id, match_type, match_status)
                  OUTPUT inserted.search_result_id VALUES (N'cas049-test', N'cas049-test', @taskId, @fileIndexId, @matchType, @status)",
                new { taskId, fileIndexId, matchType, status });
        }

        return (fileIndexId, searchResultId);
    }

    /// <summary>Set-based seed of <paramref name="count"/> matched files directly in <paramref name="folderUnderOperations"/>.</summary>
    private async Task SeedBulkMatchesAsync(int taskId, string folderUnderOperations, int count)
    {
        var folderPath = SearchBrowseDbWebApplicationFactory.Prefix + "/" + folderUnderOperations;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            @"INSERT INTO dbo.file_index
                (created_by, updated_by, source, bucket_name, relative_path, file_name, file_extension,
                 file_size_bytes, modified_at, last_seen_at, is_active)
              SELECT N'cas049-test', N'cas049-test', N'B2', @bucket,
                     @folderPath + N'/f' + RIGHT(N'0000000' + CAST(n AS NVARCHAR(10)), 7) + N'.txt',
                     N'f' + RIGHT(N'0000000' + CAST(n AS NVARCHAR(10)), 7) + N'.txt', N'txt',
                     10, SYSUTCDATETIME(), SYSUTCDATETIME(), 1
              FROM (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c) t",
            new { bucket = _bucket, folderPath, count }, commandTimeout: 300);
        await connection.ExecuteAsync(
            @"INSERT INTO dbo.search_result (created_by, updated_by, task_queue_id, file_index_id, match_type, match_status)
              SELECT N'cas049-test', N'cas049-test', @taskId, fi.file_index_id, N'Filename', N'Flagged'
              FROM dbo.file_index fi
              WHERE fi.bucket_name = @bucket AND fi.relative_path LIKE @folderPath + N'/f%'",
            new { taskId, bucket = _bucket, folderPath }, commandTimeout: 300);
    }

    // ---- HTTP helpers ----------------------------------------------------------------------------------

    private async Task<string> LoginAsync(string? username = null)
    {
        var response = await _client!.PostAsJsonAsync("/login", new LoginRequest { Username = username ?? _username, Password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/B2 fakes are configured to succeed");
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        return values!.First(v => v.StartsWith(SearchBrowseDbWebApplicationFactory.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? cookie, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client!.SendAsync(request);
    }

    private static string BrowseUrl(int taskId, string? folder = null, bool? foldersOnly = null)
    {
        var query = new List<string>();
        if (folder is not null)
        {
            query.Add("folder=" + Uri.EscapeDataString(folder));
        }

        if (foldersOnly is not null)
        {
            query.Add("foldersOnly=" + (foldersOnly.Value ? "true" : "false"));
        }

        return $"/search/{taskId}/browse" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
    }

    private static string IdsUrl(int taskId, string? folder = null) =>
        $"/search/{taskId}/browse/result-ids" + (folder is null ? string.Empty : "?folder=" + Uri.EscapeDataString(folder));

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string> ProblemTypeAsync(HttpResponseMessage response)
    {
        using var json = await JsonAsync(response);
        return json.RootElement.GetProperty("type").GetString()!;
    }

    private static List<string> Names(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();

    private static List<int> Ids(JsonElement root) =>
        root.GetProperty("searchResultIds").EnumerateArray().Select(e => e.GetInt32()).ToList();

    private async Task<JsonDocument> BrowseOkAsync(string cookie, int taskId, string? folder = null, bool? foldersOnly = null)
    {
        using var response = await SendAsync(HttpMethod.Get, BrowseUrl(taskId, folder, foldersOnly), cookie);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await JsonAsync(response);
    }

    private async Task<List<int>> IdsOkAsync(string cookie, int taskId, string? folder)
    {
        using var response = await SendAsync(HttpMethod.Get, IdsUrl(taskId, folder), cookie);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var ids = Ids(json.RootElement);
        json.RootElement.GetProperty("count").GetInt32().Should().Be(ids.Count);
        return ids;
    }

    // ---- SC-1 / SC-2 -----------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC1_ResultsAppearAsFolderHierarchy()
    {
        var taskId = await SeedTaskAsync();
        await SeedMatchAsync(taskId, "A/B/x.xlsx");
        await SeedMatchAsync(taskId, "C/y.pdf");
        var cookie = await LoginAsync();

        using var root = await BrowseOkAsync(cookie, taskId);
        using var folderA = await BrowseOkAsync(cookie, taskId, "A");
        using var folderAB = await BrowseOkAsync(cookie, taskId, "A/B");
        using var rootFoldersOnly = await BrowseOkAsync(cookie, taskId, null, true);

        root.RootElement.GetProperty("rootName").GetString().Should().Be("Operations");
        Names(root.RootElement.GetProperty("folders")).Should().Equal("A", "C");
        root.RootElement.GetProperty("files").GetArrayLength().Should().Be(0);
        root.RootElement.GetProperty("totalFolders").GetInt32().Should().Be(2);

        Names(folderA.RootElement.GetProperty("folders")).Should().Equal("B");
        folderA.RootElement.GetProperty("folders")[0].GetProperty("path").GetString().Should().Be("A/B");
        folderA.RootElement.GetProperty("folders")[0].GetProperty("hasSubfolders").GetBoolean().Should().BeFalse();
        root.RootElement.GetProperty("folders")[0].GetProperty("hasSubfolders").GetBoolean().Should().BeTrue("A leads to B");
        root.RootElement.GetProperty("folders")[1].GetProperty("hasSubfolders").GetBoolean().Should().BeFalse("C holds only a file");
        folderA.RootElement.GetProperty("files").GetArrayLength().Should().Be(0);
        folderA.RootElement.GetProperty("parent").GetString().Should().Be(string.Empty);

        folderAB.RootElement.GetProperty("folders").GetArrayLength().Should().Be(0);
        Names(folderAB.RootElement.GetProperty("files")).Should().Equal("x.xlsx");
        folderAB.RootElement.GetProperty("parent").GetString().Should().Be("A");
        folderAB.RootElement.GetProperty("breadcrumb").GetArrayLength().Should().Be(2);

        Names(rootFoldersOnly.RootElement.GetProperty("folders")).Should().Equal("A", "C");
        rootFoldersOnly.RootElement.GetProperty("files").GetArrayLength().Should().Be(0);
    }

    [RealDbFact]
    public async Task Test_SC2_OnlyMatchesAndTheirAncestors()
    {
        var taskId = await SeedTaskAsync();
        var otherTaskId = await SeedTaskAsync();
        await SeedMatchAsync(taskId, "A/B/match.txt");
        await SeedMatchAsync(null, "A/B/unmatched-same-folder.txt");     // indexed but not a match
        await SeedMatchAsync(null, "A/Unmatched/deep/file.txt");         // unmatched sibling folder
        await SeedMatchAsync(null, "Z/never.txt");                       // unmatched root folder
        await SeedMatchAsync(otherTaskId, "A/OtherTasksFolder/m.txt");   // matched by a different task
        await SeedMatchAsync(taskId, "A/Inactive/gone.txt", active: false);
        await SeedMatchAsync(taskId, "A/ODrive/odrive.txt", source: "ODrive");
        await SeedMatchAsync(taskId, "root-match.txt");
        await SeedMatchAsync(null, "root-unmatched.txt");
        var cookie = await LoginAsync();

        using var root = await BrowseOkAsync(cookie, taskId);
        using var folderA = await BrowseOkAsync(cookie, taskId, "A");
        using var folderAB = await BrowseOkAsync(cookie, taskId, "A/B");

        Names(root.RootElement.GetProperty("folders")).Should().Equal("A");
        Names(root.RootElement.GetProperty("files")).Should().Equal("root-match.txt");
        Names(folderA.RootElement.GetProperty("folders")).Should().Equal("B");
        Names(folderAB.RootElement.GetProperty("files")).Should().Equal("match.txt");
        folderA.RootElement.GetProperty("totalFiles").GetInt32().Should().Be(0);
    }

    // ---- SC-3 ------------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC3_FileItemsCarrySearchResultId()
    {
        var taskId = await SeedTaskAsync();
        var (_, flaggedId) = await SeedMatchAsync(taskId, "Docs/flagged.txt", status: "Flagged");
        var (_, reviewId) = await SeedMatchAsync(taskId, "Docs/review.txt", status: "Needs Review");
        var cookie = await LoginAsync();

        using var docs = await BrowseOkAsync(cookie, taskId, "Docs");

        var files = docs.RootElement.GetProperty("files").EnumerateArray().ToList();
        files.Select(f => (f.GetProperty("name").GetString(), f.GetProperty("searchResultId").GetInt32(), f.GetProperty("matchStatus").GetString()))
            .Should().Equal(("flagged.txt", flaggedId, "Flagged"), ("review.txt", reviewId, "Needs Review"));
        files[0].GetProperty("extension").GetString().Should().Be("txt");

        // The id is accepted by the existing quarantine endpoint for the same task.
        using var quarantine = await SendAsync(HttpMethod.Post, "/search/results/quarantine", cookie,
            new QuarantineAddRequest { TaskId = taskId, FileIds = new[] { flaggedId } });
        quarantine.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = await JsonAsync(quarantine);
        body.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        await using var connection = new SqlConnection(ConnectionString);
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.quarantine_entry WHERE search_result_id = @flaggedId AND status = N'Active'", new { flaggedId }))
            .Should().Be(1);
    }

    // ---- SC-4 ------------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC4_OwnershipAndCompletionEnforced()
    {
        var foreignTask = await SeedTaskAsync(userId: _otherUserId);
        var queuedTask = await SeedTaskAsync(status: "Queued");
        var runningTask = await SeedTaskAsync(status: "Running");
        var failedTask = await SeedTaskAsync(status: "Failed");
        var syncTask = await SeedTaskAsync(type: "SyncCopy");
        await SeedMatchAsync(foreignTask, "A/x.txt");
        var cookie = await LoginAsync();

        using var foreign = await SendAsync(HttpMethod.Get, BrowseUrl(foreignTask), cookie);
        using var missing = await SendAsync(HttpMethod.Get, BrowseUrl(999999999), cookie);
        using var wrongType = await SendAsync(HttpMethod.Get, BrowseUrl(syncTask), cookie);
        using var queued = await SendAsync(HttpMethod.Get, BrowseUrl(queuedTask), cookie);
        using var running = await SendAsync(HttpMethod.Get, BrowseUrl(runningTask), cookie);
        using var failed = await SendAsync(HttpMethod.Get, BrowseUrl(failedTask), cookie);
        using var anonymous = await SendAsync(HttpMethod.Get, BrowseUrl(foreignTask), cookie: null);

        foreach (var response in new[] { foreign, missing, wrongType })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ProblemTypeAsync(response)).Should().Be(ErrorBase + "task-not-found");
        }

        var bodies = new[] { foreign, missing, wrongType }.Select(r => r.Content.ReadAsStringAsync().Result).ToList();
        bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("title").GetString()).Distinct()
            .Should().HaveCount(1, "missing, not-owned and wrong-type tasks give the same body");
        foreach (var response in new[] { queued, running, failed })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ProblemTypeAsync(response)).Should().Be(ErrorBase + "task-not-complete");
        }

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-5 ------------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC5_LargeResultSetBrowsesInOneCall()
    {
        const int count = 100_000;
        var taskId = await SeedTaskAsync();
        var seedWatch = Stopwatch.StartNew();
        await SeedBulkMatchesAsync(taskId, "Big", count);
        await SeedMatchAsync(taskId, "Big/Sub/one.txt");
        seedWatch.Stop();
        var cookie = await LoginAsync();

        var watch = Stopwatch.StartNew();
        using var response = await SendAsync(HttpMethod.Get, BrowseUrl(taskId, "Big"), cookie);
        var body = await response.Content.ReadAsStringAsync();
        watch.Stop();
        var rootWatch = Stopwatch.StartNew();
        using var root = await BrowseOkAsync(cookie, taskId);
        rootWatch.Stop();

        _output.WriteLine($"CAS-049 SC-5: seeded {count + 1} matches in {seedWatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"CAS-049 SC-5: GET browse?folder=Big ({count} files in one response, {body.Length / 1024} KiB) = {watch.ElapsedMilliseconds} ms");
        _output.WriteLine($"CAS-049 SC-5: GET browse (root, 1 folder) = {rootWatch.ElapsedMilliseconds} ms");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("files").GetArrayLength().Should().Be(count, "one response, no paging");
        json.RootElement.GetProperty("totalFiles").GetInt32().Should().Be(count);
        Names(json.RootElement.GetProperty("folders")).Should().Equal("Sub");
        Names(root.RootElement.GetProperty("folders")).Should().Equal("Big");
    }

    // ---- SC-6 ------------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC6_LegacyDuplicateMatchListedOnce()
    {
        var taskId = await SeedTaskAsync();
        var (fileIndexId, filenameId) = await SeedMatchAsync(taskId, "Legacy/dup.txt", "Filename");
        var (_, contentId) = await SeedMatchAsync(taskId, "Legacy/dup.txt", "Content", "Needs Review", existingFileIndexId: fileIndexId);
        var cookie = await LoginAsync();

        using var legacy = await BrowseOkAsync(cookie, taskId, "Legacy");
        using var root = await BrowseOkAsync(cookie, taskId);
        var ids = await IdsOkAsync(cookie, taskId, "Legacy");

        contentId.Should().BeGreaterThan(filenameId);
        var file = legacy.RootElement.GetProperty("files").EnumerateArray().Should().ContainSingle().Subject;
        file.GetProperty("fileIndexId").GetInt32().Should().Be(fileIndexId);
        file.GetProperty("searchResultId").GetInt32().Should().Be(filenameId, "the lowest search_result_id is kept");
        legacy.RootElement.GetProperty("totalFiles").GetInt32().Should().Be(1);
        Names(root.RootElement.GetProperty("folders")).Should().Equal("Legacy");
        ids.Should().Equal(filenameId);
    }

    // ---- SC-7 ------------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC7_NoServerPathOrTermInResponse()
    {
        var taskId = await SeedTaskAsync();
        await SeedMatchAsync(taskId, "Deep/er/file.txt");
        await SeedMatchAsync(taskId, "top.txt");
        var cookie = await LoginAsync();
        var responses = new List<string>();
        foreach (var url in new[]
                 {
                     BrowseUrl(taskId), BrowseUrl(taskId, "Deep"), BrowseUrl(taskId, "Deep/er"),
                     BrowseUrl(taskId, "Deep", true), IdsUrl(taskId, "Deep"), IdsUrl(taskId), BrowseUrl(taskId, "Nope")
                 })
        {
            using var response = await SendAsync(HttpMethod.Get, url, cookie);
            responses.Add(await response.Content.ReadAsStringAsync());
        }

        foreach (var body in responses)
        {
            body.Should().NotContain("D/Shares").And.NotContain(SearchBrowseDbWebApplicationFactory.Prefix);
            body.Should().NotContainEquivalentOf(_bucket);
            body.Should().NotContainEquivalentOf(Term);
            body.Should().NotContain("relativePath").And.NotContain("relative_path");
            body.Should().NotContain("4_secretB2token_neverLog").And.NotContain("plain-app-key").And.NotContain("plain-key-id");
            Regex.IsMatch(body, @"[A-Za-z]:\\\\").Should().BeFalse("no drive letter path may appear");
        }
    }

    // ---- SC-8 ------------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC8_ResultIdsIncludeAllDepths()
    {
        var taskId = await SeedTaskAsync();
        var (_, direct) = await SeedMatchAsync(taskId, "A/x.txt");
        var (_, deep) = await SeedMatchAsync(taskId, "A/B/y.txt");
        var (_, deeper) = await SeedMatchAsync(taskId, "A/B/C/z.txt");
        var (_, sibling) = await SeedMatchAsync(taskId, "C/w.txt");
        var (_, nearName) = await SeedMatchAsync(taskId, "A2/near.txt");   // shares the "A" text prefix, not the folder
        await SeedMatchAsync(taskId, "A/inactive.txt", active: false);
        var cookie = await LoginAsync();

        var folderA = await IdsOkAsync(cookie, taskId, "A");
        var folderAB = await IdsOkAsync(cookie, taskId, "A/B");
        var wholeTask = await IdsOkAsync(cookie, taskId, null);

        folderA.Should().Equal(new[] { direct, deep, deeper }.OrderBy(i => i));
        folderA.Should().NotContain(new[] { sibling, nearName });
        folderAB.Should().Equal(new[] { deep, deeper }.OrderBy(i => i));
        wholeTask.Should().Equal(new[] { direct, deep, deeper, sibling, nearName }.OrderBy(i => i));
    }

    // ---- SC-9 / V8 -------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC9_ResultIdsCapBoundary()
    {
        var taskId = await SeedTaskAsync();
        await SeedBulkMatchesAsync(taskId, "Exact/Nest", ISearchResultBrowseService.ResultIdsLimit - 1);
        await SeedMatchAsync(taskId, "Exact/direct.txt");            // 1,000 beneath Exact at mixed depth
        await SeedBulkMatchesAsync(taskId, "Over/Nest", ISearchResultBrowseService.ResultIdsLimit);
        await SeedMatchAsync(taskId, "Over/direct.txt");             // 1,001 beneath Over
        var cookie = await LoginAsync();

        using var exact = await SendAsync(HttpMethod.Get, IdsUrl(taskId, "Exact"), cookie);
        using var over = await SendAsync(HttpMethod.Get, IdsUrl(taskId, "Over"), cookie);

        exact.StatusCode.Should().Be(HttpStatusCode.OK, "exactly 1,000 is allowed");
        using (var json = await JsonAsync(exact))
        {
            Ids(json.RootElement).Should().HaveCount(1000).And.OnlyHaveUniqueItems().And.BeInAscendingOrder();
            json.RootElement.GetProperty("count").GetInt32().Should().Be(1000);
        }

        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(over)).Should().Be(ErrorBase + "selection-too-large");
    }

    [RealDbFact]
    public async Task Test_V8_OverCapReturns400WithoutIds()
    {
        var taskId = await SeedTaskAsync();
        await SeedBulkMatchesAsync(taskId, "Many", ISearchResultBrowseService.ResultIdsLimit + 1);
        await SeedMatchAsync(taskId, "Few/one.txt");
        var cookie = await LoginAsync();

        using var over = await SendAsync(HttpMethod.Get, IdsUrl(taskId, "Many"), cookie);
        using var wholeTaskOver = await SendAsync(HttpMethod.Get, IdsUrl(taskId), cookie);
        using var few = await SendAsync(HttpMethod.Get, IdsUrl(taskId, "Few"), cookie);

        foreach (var response in new[] { over, wholeTaskOver })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            json.RootElement.GetProperty("type").GetString().Should().Be(ErrorBase + "selection-too-large");
            json.RootElement.GetProperty("limit").GetInt32().Should().Be(1000);
            json.RootElement.TryGetProperty("searchResultIds", out _).Should().BeFalse("no ids are returned above the cap");
            body.Should().NotContain("count\"");
        }

        few.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- SC-10 -----------------------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_SC10_ResultIdsFeedQuarantineAtCap()
    {
        var taskId = await SeedTaskAsync();
        await SeedBulkMatchesAsync(taskId, "Cap/Sub", ISearchResultBrowseService.ResultIdsLimit - 1);
        await SeedMatchAsync(taskId, "Cap/top.txt");
        var cookie = await LoginAsync();

        var idsWatch = Stopwatch.StartNew();
        var ids = await IdsOkAsync(cookie, taskId, "Cap");
        idsWatch.Stop();
        var quarantineWatch = Stopwatch.StartNew();
        using var quarantine = await SendAsync(HttpMethod.Post, "/search/results/quarantine", cookie,
            new QuarantineAddRequest { TaskId = taskId, FileIds = ids });
        quarantineWatch.Stop();
        var quarantineBody = await quarantine.Content.ReadAsStringAsync();

        _output.WriteLine($"CAS-049 SC-10: GET result-ids ({ids.Count} ids) = {idsWatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"CAS-049 SC-10: POST /search/results/quarantine ({ids.Count} ids, one request) = {quarantineWatch.ElapsedMilliseconds} ms (client timeout 300000 ms)");
        ids.Should().HaveCount(1000);
        quarantine.StatusCode.Should().Be(HttpStatusCode.OK, quarantineBody.Length > 300 ? quarantineBody[..300] : quarantineBody);
        using var json = JsonDocument.Parse(quarantineBody);
        json.RootElement.GetProperty("items").GetArrayLength().Should().Be(1000);
        (json.RootElement.TryGetProperty("warnings", out var warnings) ? warnings.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null, "nothing was skipped");
        await using var connection = new SqlConnection(ConnectionString);
        (await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM dbo.quarantine_entry q JOIN dbo.search_result sr ON sr.search_result_id = q.search_result_id
              WHERE sr.task_queue_id = @taskId AND q.status = N'Active'", new { taskId })).Should().Be(1000);
        await _factory!.AuditLogService.Received(1000).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineAdd && e.TaskQueueId == taskId), Arg.Any<CancellationToken>());
    }

    // ---- V4 / V5 / V7 / V9 ----------------------------------------------------------------------------

    [RealDbFact]
    public async Task Test_V4_FolderWithNoMatchesReturns404()
    {
        var taskId = await SeedTaskAsync();
        var otherTaskId = await SeedTaskAsync();
        await SeedMatchAsync(taskId, "Has/match.txt");
        await SeedMatchAsync(null, "Unmatched/indexed-only.txt");
        await SeedMatchAsync(otherTaskId, "OtherTask/m.txt");
        await SeedMatchAsync(taskId, "Inactive/gone.txt", active: false);
        var cookie = await LoginAsync();

        foreach (var folder in new[] { "Unmatched", "OtherTask", "Inactive", "No/Such/Folder", "Has/missing" })
        {
            foreach (var foldersOnly in new bool?[] { null, true })
            {
                using var response = await SendAsync(HttpMethod.Get, BrowseUrl(taskId, folder, foldersOnly), cookie);
                response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"folder '{folder}' (foldersOnly={foldersOnly}) holds no match");
                (await ProblemTypeAsync(response)).Should().Be(ErrorBase + "folder-not-found");
            }
        }

        // A leaf folder that does hold a match is 200 even with foldersOnly; an empty task root is 200.
        using var leaf = await BrowseOkAsync(cookie, taskId, "Has", true);
        leaf.RootElement.GetProperty("folders").GetArrayLength().Should().Be(0);
        var emptyTask = await SeedTaskAsync();
        using var emptyRoot = await BrowseOkAsync(cookie, emptyTask);
        emptyRoot.RootElement.GetProperty("folders").GetArrayLength().Should().Be(0);
        emptyRoot.RootElement.GetProperty("files").GetArrayLength().Should().Be(0);
    }

    [RealDbFact]
    public async Task Test_V5_WildcardsInFolderAreLiteral()
    {
        var taskId = await SeedTaskAsync();
        await SeedMatchAsync(taskId, "100%_done/in.txt");
        await SeedMatchAsync(taskId, "100Xadone/decoy.txt");
        await SeedMatchAsync(taskId, "a[b]/c.txt");
        await SeedMatchAsync(taskId, "ab/decoy2.txt");
        await SeedMatchAsync(taskId, "u_s/d.txt");
        await SeedMatchAsync(taskId, "uxs/decoy3.txt");
        var cookie = await LoginAsync();

        foreach (var (folder, expected) in new[] { ("100%_done", "in.txt"), ("a[b]", "c.txt"), ("u_s", "d.txt") })
        {
            using var page = await BrowseOkAsync(cookie, taskId, folder);
            Names(page.RootElement.GetProperty("files")).Should().Equal(expected);
        }

        using var wildcardOnly = await SendAsync(HttpMethod.Get, BrowseUrl(taskId, "1%"), cookie);
        wildcardOnly.StatusCode.Should().Be(HttpStatusCode.NotFound, "'%' must not act as a wildcard");
        using var underscoreOnly = await SendAsync(HttpMethod.Get, BrowseUrl(taskId, "u_"), cookie);
        underscoreOnly.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var root = await BrowseOkAsync(cookie, taskId);
        Names(root.RootElement.GetProperty("folders")).Should().BeEquivalentTo("100%_done", "100Xadone", "a[b]", "ab", "u_s", "uxs");
    }

    [RealDbFact]
    public async Task Test_V7_ResultIdsSharesBrowseGuards()
    {
        var foreignTask = await SeedTaskAsync(userId: _otherUserId);
        var queuedTask = await SeedTaskAsync(status: "Queued");
        var taskId = await SeedTaskAsync();
        await SeedMatchAsync(foreignTask, "A/x.txt");
        var (_, literal) = await SeedMatchAsync(taskId, "100%_done/in.txt");
        await SeedMatchAsync(taskId, "100Xadone/decoy.txt");
        await SeedMatchAsync(null, "Unmatched/indexed-only.txt");
        var cookie = await LoginAsync();

        async Task<(HttpStatusCode Status, string Type)> Probe(string url)
        {
            using var response = await SendAsync(HttpMethod.Get, url, cookie);
            return (response.StatusCode, response.IsSuccessStatusCode ? string.Empty : await ProblemTypeAsync(response));
        }

        (await Probe(IdsUrl(foreignTask, "A"))).Should().Be((HttpStatusCode.NotFound, ErrorBase + "task-not-found"));
        (await Probe(IdsUrl(999999999))).Should().Be((HttpStatusCode.NotFound, ErrorBase + "task-not-found"));
        (await Probe(IdsUrl(queuedTask))).Should().Be((HttpStatusCode.Conflict, ErrorBase + "task-not-complete"));
        (await Probe(IdsUrl(taskId, "A/../Secret"))).Should().Be((HttpStatusCode.BadRequest, ErrorBase + "validation-failed"));
        (await Probe(IdsUrl(taskId, "/A"))).Should().Be((HttpStatusCode.BadRequest, ErrorBase + "validation-failed"));
        (await Probe(IdsUrl(taskId, "A\\B"))).Should().Be((HttpStatusCode.BadRequest, ErrorBase + "validation-failed"));
        (await Probe(IdsUrl(taskId, "Unmatched"))).Should().Be((HttpStatusCode.NotFound, ErrorBase + "folder-not-found"));
        (await Probe(IdsUrl(taskId, "1%"))).Should().Be((HttpStatusCode.NotFound, ErrorBase + "folder-not-found"));
        (await IdsOkAsync(cookie, taskId, "100%_done")).Should().Equal(literal);
        using var anonymous = await SendAsync(HttpMethod.Get, IdsUrl(taskId), cookie: null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [RealDbFact]
    public async Task Test_V9_IdsScopedToTask()
    {
        var taskId = await SeedTaskAsync();
        var otherTaskId = await SeedTaskAsync(userId: _otherUserId);
        var (sharedFile, mine) = await SeedMatchAsync(taskId, "Shared/both.txt");
        var (_, theirs) = await SeedMatchAsync(otherTaskId, "Shared/both.txt", existingFileIndexId: sharedFile);
        var (_, theirOnly) = await SeedMatchAsync(otherTaskId, "Shared/theirs.txt");
        var (_, mineDeep) = await SeedMatchAsync(taskId, "Shared/Sub/mine.txt");
        var cookie = await LoginAsync();

        var ids = await IdsOkAsync(cookie, taskId, "Shared");

        ids.Should().Equal(new[] { mine, mineDeep }.OrderBy(i => i));
        ids.Should().NotContain(new[] { theirs, theirOnly });
        await using var connection = new SqlConnection(ConnectionString);
        (await connection.QueryAsync<int>(
            "SELECT DISTINCT task_queue_id FROM dbo.search_result WHERE search_result_id IN @ids", new { ids }))
            .Should().Equal(taskId);
    }
}
