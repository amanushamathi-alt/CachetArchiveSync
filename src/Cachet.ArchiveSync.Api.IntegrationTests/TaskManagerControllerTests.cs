using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-016 §8's <c>POST /tasks/{taskId}/restart</c>, run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via
/// <see cref="TaskManagerApiWebApplicationFactory"/> (see that class's XML doc). Proves the spec's
/// §13 Test Plan "TaskManagerControllerTests" assignment: V2, SC-4/V3, plus standard 401/403/409
/// HTTP-level branch coverage matching this codebase's other controller test files (e.g.
/// <c>QuarantineControllerTests</c>, <c>AuditLogControllerTests</c>).
///
/// <para>
/// CAS-030 §13 adds SC-1, SC-2, SC-3, V3, V4, V5 (real LocalDB via
/// <see cref="RealDbFactAttribute"/> — the real <c>TaskQueueRepository</c> and stored procedures,
/// only the Auth-surface fakes swapped) and V6 (no DB needed). Joins the CAS-028 non-parallel
/// real-DB collection because those suites claim/sweep <c>Queued</c>/<c>Running</c> rows
/// database-wide, which would corrupt this class's counts if interleaved.
/// </para>
/// </summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskManagerControllerTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private static readonly PasswordHasher<object> Hasher = new();

    private TaskManagerApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new TaskManagerApiWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        if (_realDbClient is not null)
        {
            _realDbClient.Dispose();
        }

        if (_realDbFactory is not null)
        {
            await _realDbFactory.DisposeAsync();
        }

        await CleanUpRealDbRowsAsync();
    }

    private void SeedUser(int userId, string username = ValidUsername) => SeedUser(_factory, userId, username);

    private static void SeedUser(TaskManagerApiWebApplicationFactory factory, int userId, string username)
    {
        var sessionTokenVersion = factory.RegisterSessionTokenVersion(userId);

        factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = userId,
            Username = username,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = sessionTokenVersion
        });
        factory.CredentialProtector.Unprotect("ct:keyid").Returns("plain-key-id");
        factory.CredentialProtector.Unprotect("ct:key").Returns("plain-app-key");
        factory.B2KeyAuthorizer
            .AuthorizeAsync("plain-key-id", "plain-app-key", Arg.Any<CancellationToken>())
            .Returns(new B2AuthorizationInfo
            {
                AccountId = "acct-1",
                AuthorizationToken = "4_secretB2token_neverLog",
                ApiUrl = "https://api-fake.backblazeb2.com",
                DownloadUrl = "https://download-fake.backblazeb2.com",
                Capabilities = new[] { "listFiles", "readFiles" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });
    }

    private Task<string> LoginAsync(string username = ValidUsername) => LoginAsync(_client, username);

    private static async Task<string> LoginAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/B2 fakes are configured to succeed");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(TaskManagerApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
        cookie.Should().NotBeNull();
        return cookie!.Split(';')[0];
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url, string cookieHeaderValue)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeaderValue);
        return request;
    }

    // ---- V2: restarting a taskId that does not exist returns 404, never 500 or a silent no-op ------

    [Fact]
    public async Task Test_V2_RestartReturns404ForUnknownTask()
    {
        // Arrange
        SeedUser(userId: 1);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository
            .RestartAsync(999999, 1, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.NotFound });

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, "/tasks/999999/restart", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.TaskRestart), Arg.Any<CancellationToken>());
    }

    // ---- SC-4/V3: a successful restart audits before the 202, referencing the NEW row's id ----------

    [Fact]
    public async Task Test_SC4_RestartAuditRowPrecedesResponseAndReferencesNewRow()
    {
        // Arrange
        SeedUser(userId: 9);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository
            .RestartAsync(100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.Restarted, NewTaskQueueId = 100999 });

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, "/tasks/100234/restart", cookie));

        // Assert — 202 body shape (§8) and the audit row references the NEW task id, not the
        // original path parameter, written before this response was ever produced.
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<TaskRestartResponseShape>();
        body!.taskId.Should().Be(100999);
        body.restartedFromTaskId.Should().Be(100234);

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.TaskRestart &&
                e.TabModule == "Task Manager" &&
                e.TaskQueueId == 100999 &&
                e.UserId == 9 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }

    private sealed record TaskRestartResponseShape(int taskId, int restartedFromTaskId);

    // ---- SC-3 (409 branch, HTTP-level): a non-Failed task is rejected without creating a new row ----

    [Fact]
    public async Task Test_Restart_NotFailedStatus_Returns409()
    {
        // Arrange
        SeedUser(userId: 1);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository
            .RestartAsync(100234, 1, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.NotRestartable });

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, "/tasks/100234/restart", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.TaskRestart), Arg.Any<CancellationToken>());
    }

    // ---- SC-5/V5 (HTTP-level): a non-owner is rejected with 403, regardless of the task's status ----

    [Fact]
    public async Task Test_SC5_RestartReturns403ForNonOwningUser()
    {
        // Arrange
        SeedUser(userId: 7);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository
            .RestartAsync(100234, 7, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.NotOwner });

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post, "/tasks/100234/restart", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.TaskRestart), Arg.Any<CancellationToken>());
    }

    // ---- Standard 401 branch: no session, no repository call at all ---------------------------------

    [Fact]
    public async Task Test_PostRestart_NoSession_Returns401()
    {
        // Act
        using var response = await _client.PostAsync("/tasks/100234/restart", content: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.TaskQueueRepository.DidNotReceive().RestartAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    // =================================================================================================
    // CAS-030 §6/§13 — GET /tasks, GET /tasks/{taskId}, GET /tasks/summary, POST /tasks/{taskId}/acknowledge
    // =================================================================================================

    private TaskManagerApiWebApplicationFactory? _realDbFactory;
    private HttpClient? _realDbClient;
    private readonly List<int> _realDbUserIds = new();

    private static string RealDbConnectionString => RealDbConfiguration.ConnectionString!;

    private sealed record RealDbUser(int UserId, string Username, string Cookie);

    /// <summary>
    /// Inserts a real <c>dbo.users</c> row (so <c>dbo.task_queue_lst</c>'s username join and the
    /// task FK resolve), mirrors it into the factory's fake <see cref="Core.Interfaces.IUserRepository"/>
    /// for <c>POST /login</c>, and returns an authenticated session cookie for it.
    /// </summary>
    private async Task<RealDbUser> CreateRealDbUserAsync()
    {
        if (_realDbFactory is null)
        {
            _realDbFactory = new TaskManagerApiWebApplicationFactory(RealDbConnectionString);
            _realDbClient = _realDbFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        }

        var username = "cas030-" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = new SqlConnection(RealDbConnectionString);
        var userId = await connection.QuerySingleAsync<int>(
            "INSERT INTO dbo.users (created_by, updated_by, username, password_hash, b2_application_key_id, b2_application_key) " +
            "OUTPUT inserted.user_id VALUES (N'test-fixture', N'test-fixture', @username, N'x', N'ct:keyid', N'ct:key')",
            new { username });
        _realDbUserIds.Add(userId);

        SeedUser(_realDbFactory, userId, username);
        var cookie = await LoginAsync(_realDbClient!, username);
        return new RealDbUser(userId, username, cookie);
    }

    /// <summary>
    /// Inserts via <c>dbo.task_queue_ins</c>, then sets status/timestamps directly for setup (the same
    /// "insert, then adjust" convention as <c>TaskQueueRestartRepositoryTests</c>).
    /// </summary>
    private static async Task<int> InsertRealDbTaskAsync(
        int userId,
        string taskType,
        string status,
        DateTime submittedAtUtc,
        bool acknowledged = false,
        string? requestParameters = null)
    {
        await using var connection = new SqlConnection(RealDbConnectionString);
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", taskType);
        parameters.Add("@submitted_by_user_id", userId);
        parameters.Add("@request_parameters", requestParameters);
        parameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: CommandType.StoredProcedure));
        var taskId = parameters.Get<int>("@task_queue_id");

        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = @status, submitted_at = @submittedAtUtc, " +
            "started_at = CASE WHEN @status = N'Queued' THEN NULL ELSE DATEADD(SECOND, 2, @submittedAtUtc) END, " +
            "completed_at = CASE WHEN @status IN (N'Completed', N'Failed', N'Cancelled') THEN DATEADD(MINUTE, 5, @submittedAtUtc) ELSE NULL END, " +
            "result_acknowledged_at = CASE WHEN @acknowledged = 1 THEN DATEADD(MINUTE, 6, @submittedAtUtc) ELSE NULL END " +
            "WHERE task_queue_id = @taskId",
            new { status, submittedAtUtc, acknowledged, taskId });

        return taskId;
    }

    private static async Task<DateTime?> ReadAcknowledgedAtAsync(int taskId)
    {
        await using var connection = new SqlConnection(RealDbConnectionString);
        return await connection.QuerySingleAsync<DateTime?>(
            "SELECT result_acknowledged_at FROM dbo.task_queue WHERE task_queue_id = @taskId", new { taskId });
    }

    private async Task CleanUpRealDbRowsAsync()
    {
        if (_realDbUserIds.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(RealDbConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM dbo.task_queue WHERE submitted_by_user_id IN @ids; DELETE FROM dbo.users WHERE user_id IN @ids;",
            new { ids = _realDbUserIds });
    }

    private Task<HttpResponseMessage> SendRealDbAsync(HttpMethod method, string url, RealDbUser user) =>
        _realDbClient!.SendAsync(AuthenticatedRequest(method, url, user.Cookie));

    private async Task<TaskSummaryResponse> GetSummaryAsync(RealDbUser user)
    {
        using var response = await SendRealDbAsync(HttpMethod.Get, "/tasks/summary", user);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TaskSummaryResponse>())!;
    }

    /// <summary>V3: ProblemDetails carries a per-request <c>traceId</c>; every other member must match.</summary>
    private static async Task<JsonObject> ReadProblemWithoutTraceIdAsync(HttpResponseMessage response)
    {
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        node.Remove("traceId");
        return node;
    }

    // ---- SC-1: only the caller's tasks, newest first, type/status filters, username resolved --------

    [RealDbFact]
    public async Task Test_SC1_ListTasks_ReturnsOnlyCallersTasksNewestFirst()
    {
        // Arrange
        var caller = await CreateRealDbUserAsync();
        var otherUser = await CreateRealDbUserAsync();
        var now = DateTime.UtcNow;
        var oldestSearch = await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Completed", now.AddHours(-3));
        var runningScan = await InsertRealDbTaskAsync(caller.UserId, "ContentScan", "Running", now.AddHours(-2));
        var newestSearch = await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Queued", now.AddHours(-1));
        var otherUsersTask = await InsertRealDbTaskAsync(otherUser.UserId, "KeywordSearch", "Completed", now.AddMinutes(-1));

        // Act
        using var all = await SendRealDbAsync(HttpMethod.Get, "/tasks", caller);
        using var byType = await SendRealDbAsync(HttpMethod.Get, "/tasks?type=KeywordSearch", caller);
        using var byStatus = await SendRealDbAsync(HttpMethod.Get, "/tasks?status=Running", caller);
        using var latestOfType = await SendRealDbAsync(HttpMethod.Get, "/tasks?type=KeywordSearch&page=1&pageSize=1", caller);

        // Assert — own rows only, newest first by submitted_at, submittedBy = the caller's username.
        all.StatusCode.Should().Be(HttpStatusCode.OK);
        var allBody = (await all.Content.ReadFromJsonAsync<PagedResult<TaskListItem>>())!;
        allBody.Items.Select(i => i.TaskId).Should().Equal(newestSearch, runningScan, oldestSearch);
        allBody.Items.Select(i => i.TaskId).Should().NotContain(otherUsersTask);
        allBody.Items.Should().OnlyContain(i => i.SubmittedBy == caller.Username);
        allBody.TotalCount.Should().Be(3);
        allBody.Page.Should().Be(1);
        allBody.PageSize.Should().Be(50, "pageSize defaults to 50 (§6)");
        allBody.Items[0].Status.Should().Be("Queued");
        allBody.Items[0].StartTime.Should().BeNull("startTime is null while the task is Queued (§6)");
        allBody.Items[2].CompletedAt.Should().NotBeNull();
        allBody.Items.Should().OnlyContain(i => !i.IsAcknowledged);

        var byTypeBody = (await byType.Content.ReadFromJsonAsync<PagedResult<TaskListItem>>())!;
        byTypeBody.Items.Select(i => i.TaskId).Should().Equal(newestSearch, oldestSearch);
        byTypeBody.Items.Should().OnlyContain(i => i.Type == "KeywordSearch");

        var byStatusBody = (await byStatus.Content.ReadFromJsonAsync<PagedResult<TaskListItem>>())!;
        byStatusBody.Items.Select(i => i.TaskId).Should().Equal(runningScan);

        var latestBody = (await latestOfType.Content.ReadFromJsonAsync<PagedResult<TaskListItem>>())!;
        latestBody.Items.Select(i => i.TaskId).Should().Equal(newestSearch);
        latestBody.TotalCount.Should().Be(2, "totalCount is the filtered, pre-paging count");
    }

    // ---- SC-2: own Queued+Running and own unacknowledged Completed+Failed; never others' or Cancelled --

    [RealDbFact]
    public async Task Test_SC2_Summary_CountsOnlyOwnActiveAndUnacknowledgedTerminal()
    {
        // Arrange
        var caller = await CreateRealDbUserAsync();
        var otherUser = await CreateRealDbUserAsync();
        var now = DateTime.UtcNow;
        await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Queued", now.AddMinutes(-10));
        await InsertRealDbTaskAsync(caller.UserId, "ContentScan", "Running", now.AddMinutes(-9));
        await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Completed", now.AddMinutes(-8));
        await InsertRealDbTaskAsync(caller.UserId, "SyncCopy", "Failed", now.AddMinutes(-7));
        await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Completed", now.AddMinutes(-6), acknowledged: true);
        await InsertRealDbTaskAsync(caller.UserId, "ContentScan", "Cancelled", now.AddMinutes(-5));
        await InsertRealDbTaskAsync(otherUser.UserId, "KeywordSearch", "Queued", now.AddMinutes(-4));
        await InsertRealDbTaskAsync(otherUser.UserId, "KeywordSearch", "Completed", now.AddMinutes(-3));

        // Act
        var summary = await GetSummaryAsync(caller);

        // Assert
        summary.ActiveCount.Should().Be(2, "own Queued + Running only");
        summary.UnacknowledgedTerminalCount.Should().Be(2,
            "own unacknowledged Completed + Failed only — the acknowledged, Cancelled, and other user's rows are excluded");
    }

    // ---- SC-3: acknowledging an own Completed task sets the timestamp and drops the summary by 1 ------

    [RealDbFact]
    public async Task Test_SC3_Acknowledge_ClearsTaskFromSummary()
    {
        // Arrange
        var caller = await CreateRealDbUserAsync();
        var now = DateTime.UtcNow;
        var completedTask = await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Completed", now.AddMinutes(-10));
        await InsertRealDbTaskAsync(caller.UserId, "ContentScan", "Failed", now.AddMinutes(-9));
        var before = await GetSummaryAsync(caller);

        // Act
        using var ackResponse = await SendRealDbAsync(HttpMethod.Post, $"/tasks/{completedTask}/acknowledge", caller);

        // Assert
        ackResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAcknowledgedAtAsync(completedTask)).Should().NotBeNull();

        var after = await GetSummaryAsync(caller);
        before.UnacknowledgedTerminalCount.Should().Be(2);
        after.UnacknowledgedTerminalCount.Should().Be(before.UnacknowledgedTerminalCount - 1);
        after.ActiveCount.Should().Be(before.ActiveCount);

        using var detail = await SendRealDbAsync(HttpMethod.Get, $"/tasks/{completedTask}", caller);
        (await detail.Content.ReadFromJsonAsync<TaskDetailResponse>())!.IsAcknowledged.Should().BeTrue();
    }

    // ---- V3: another user's task and a missing task return the same 404 body (GET and acknowledge) ---

    [RealDbFact]
    public async Task Test_V3_OtherUsersTask_Returns404IdenticalToMissing()
    {
        // Arrange
        var caller = await CreateRealDbUserAsync();
        var owner = await CreateRealDbUserAsync();
        var otherUsersTask = await InsertRealDbTaskAsync(owner.UserId, "KeywordSearch", "Completed", DateTime.UtcNow.AddMinutes(-5),
            requestParameters: """{"Term":"invoice","Bucket":"cachet-archive-prod","Extension":"PDF"}""");
        int missingTaskId;
        await using (var connection = new SqlConnection(RealDbConnectionString))
        {
            missingTaskId = await connection.QuerySingleAsync<int>("SELECT ISNULL(MAX(task_queue_id), 0) + 1000000 FROM dbo.task_queue");
        }

        // Act
        using var getOther = await SendRealDbAsync(HttpMethod.Get, $"/tasks/{otherUsersTask}", caller);
        using var getMissing = await SendRealDbAsync(HttpMethod.Get, $"/tasks/{missingTaskId}", caller);
        using var ackOther = await SendRealDbAsync(HttpMethod.Post, $"/tasks/{otherUsersTask}/acknowledge", caller);
        using var ackMissing = await SendRealDbAsync(HttpMethod.Post, $"/tasks/{missingTaskId}/acknowledge", caller);

        // Assert
        foreach (var response in new[] { getOther, getMissing, ackOther, ackMissing })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var getOtherBody = await ReadProblemWithoutTraceIdAsync(getOther);
        getOtherBody["type"]!.GetValue<string>().Should().Be("https://cachet.tracecounts.internal/errors/task-not-found");
        getOtherBody["status"]!.GetValue<int>().Should().Be(404);

        var expected = getOtherBody.ToJsonString();
        (await ReadProblemWithoutTraceIdAsync(getMissing)).ToJsonString().Should().Be(expected);
        (await ReadProblemWithoutTraceIdAsync(ackOther)).ToJsonString().Should().Be(expected);
        (await ReadProblemWithoutTraceIdAsync(ackMissing)).ToJsonString().Should().Be(expected);

        (await ReadAcknowledgedAtAsync(otherUsersTask)).Should().BeNull("a non-owner's acknowledge must not touch the row");
    }

    // ---- V4: acknowledge of a Queued/Running/Cancelled task is 409 task-not-terminal ---------------

    [RealDbFact]
    public async Task Test_V4_AcknowledgeNonTerminalTask_Returns409()
    {
        // Arrange
        var caller = await CreateRealDbUserAsync();
        var now = DateTime.UtcNow;
        var taskIds = new[]
        {
            await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Queued", now.AddMinutes(-3)),
            await InsertRealDbTaskAsync(caller.UserId, "KeywordSearch", "Running", now.AddMinutes(-2)),
            await InsertRealDbTaskAsync(caller.UserId, "ContentScan", "Cancelled", now.AddMinutes(-1))
        };

        foreach (var taskId in taskIds)
        {
            // Act
            using var response = await SendRealDbAsync(HttpMethod.Post, $"/tasks/{taskId}/acknowledge", caller);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = await ReadProblemWithoutTraceIdAsync(response);
            body["type"]!.GetValue<string>().Should().Be("https://cachet.tracecounts.internal/errors/task-not-terminal");
            (await ReadAcknowledgedAtAsync(taskId)).Should().BeNull();
        }
    }

    // ---- V5: a second acknowledge is 204 and leaves the first timestamp unchanged --------------------

    [RealDbFact]
    public async Task Test_V5_AcknowledgeTwice_TimestampUnchanged()
    {
        // Arrange
        var caller = await CreateRealDbUserAsync();
        var taskId = await InsertRealDbTaskAsync(caller.UserId, "ContentScan", "Failed", DateTime.UtcNow.AddMinutes(-5));

        // Act
        using var first = await SendRealDbAsync(HttpMethod.Post, $"/tasks/{taskId}/acknowledge", caller);
        var firstTimestamp = await ReadAcknowledgedAtAsync(taskId);
        await Task.Delay(50); // guarantees SYSUTCDATETIME() would differ if the row were re-written
        using var second = await SendRealDbAsync(HttpMethod.Post, $"/tasks/{taskId}/acknowledge", caller);
        var secondTimestamp = await ReadAcknowledgedAtAsync(taskId);

        // Assert
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        firstTimestamp.Should().NotBeNull();
        secondTimestamp.Should().Be(firstTimestamp);
    }

    // ---- V6: all four endpoints require an authenticated session ---------------------------------

    [Theory]
    [InlineData("GET", "/tasks")]
    [InlineData("GET", "/tasks?type=KeywordSearch&page=1&pageSize=1")]
    [InlineData("GET", "/tasks/100234")]
    [InlineData("GET", "/tasks/summary")]
    [InlineData("POST", "/tasks/100234/acknowledge")]
    public async Task Test_V6_TaskEndpoints_Unauthenticated_Return401(string method, string url)
    {
        // Act
        using var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.TaskQueueRepository.ReceivedCalls().Should().BeEmpty("no task read or write may happen without a session");
    }

    // =====================================================================================================
    // CAS-026 §6.1-§6.4 — GET /tasks, GET /tasks/{taskId}, GET /tasks/summary, POST /tasks/mark-viewed.
    // Proves the spec's §13 Test Plan "TaskManagerControllerTests" assignment: SC-4, SC-5, SC-6, V4, V5,
    // V8, plus standard HTTP-level branch coverage matching this file's existing Restart-action tests
    // above. All against the REAL Api pipeline via TaskManagerApiWebApplicationFactory — ITaskQueueRepository
    // and IUserRepository are mocked (no live SQL Server reached), but TaskListService/TaskDetailService/
    // TaskBadgeService themselves are the real CAS-026 implementations resolved through DI (Program.cs),
    // so these tests exercise the actual service + controller wiring end-to-end over HTTP.
    // =====================================================================================================

    private static TaskQueueRow TaskRow(
        int taskQueueId, string taskType, string status, int submittedByUserId, DateTimeOffset submittedAt, string? resultDetail = null,
        DateTimeOffset? startedAt = null) => new()
        {
            TaskQueueId = taskQueueId,
            TaskType = taskType,
            Status = status,
            SubmittedByUserId = submittedByUserId,
            SubmittedAt = submittedAt,
            StartedAt = startedAt,
            ResultDetail = resultDetail
        };

    // ---- CAS-026 V-4 (403 for another user's task) superseded by CAS-030 V3 (same 404 as a missing task, no
    //      task enumeration); proven by Test_V3_OtherUsersTask_Returns404IdenticalToMissing above.

    // ---- V-5: GET /tasks/{taskId} for a nonexistent taskId returns 404 -------------------------------

    [Fact]
    public async Task Test_V5_GetReturns404ForUnknownTask()
    {
        // Arrange
        SeedUser(userId: 7);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(999999, Arg.Any<CancellationToken>()).Returns((TaskQueueRow?)null);

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/tasks/999999", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- SC-4: GET /tasks/summary's inProgressCount equals the caller's Queued+Running row count -----

    [Fact]
    public async Task Test_SC4_SummaryInProgressCountMatchesQueuedPlusRunning()
    {
        // Arrange — dbo.task_queue_summary_get is contracted to count Queued+Running as
        // in_progress_count; here the mock repository returns that contracted shape.
        SeedUser(userId: 7);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository
            .GetSummaryAsync(7, Arg.Any<CancellationToken>())
            .Returns(new TaskSummaryResponse { ActiveCount = 2, UnacknowledgedTerminalCount = 1 });

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/tasks/summary", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TaskSummaryResponse>();
        body!.ActiveCount.Should().Be(2);
        body.UnacknowledgedTerminalCount.Should().Be(1);
    }

    // ---- CAS-026 SC-5 (mark-viewed resets unviewed count) superseded by CAS-030's per-task acknowledge;
    //      proven by Test_SC2_Summary_CountsOnlyOwnActiveAndUnacknowledgedTerminal and Test_SC3_Acknowledge_ClearsTaskFromSummary above.

    // ---- SC-6: task state (durably persisted) is visible from a fresh session, not tied to the
    // session that submitted it (FR-20). Session 1 is discarded client-side (as a closed browser would
    // do) and a completely independent POST /login establishes session 2 against the same underlying
    // (mocked, standing in for durable SQL Server) repository state; the identical task detail/list
    // data is still returned via the brand-new session's own cookie, proving the data's persistence is
    // not tied to any particular session. (A real server-side session *revocation* round trip via
    // POST /logout is CAS-002's own concern, already covered by AuditLog/AuthIntegrationTests — not
    // re-tested here, since that would only prove logout works, not that task state outlives a
    // session.) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC6_TaskStateSurvivesAcrossSessions()
    {
        // Arrange
        SeedUser(userId: 9);
        var submittedAt = new DateTimeOffset(2026, 9, 24, 14, 5, 3, TimeSpan.Zero);
        var startedAt = submittedAt.AddMinutes(1);
        _factory.TaskQueueRepository
            .GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(TaskRow(100234, "KeywordSearch", "Completed", submittedByUserId: 9, submittedAt, startedAt: startedAt));
        _factory.TaskQueueRepository
            .ListAsync(9, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<TaskQueueListRow>
            {
                new()
                {
                    TaskQueueId = 100234,
                    TaskType = "KeywordSearch",
                    SubmittedByUserId = 9,
                    Username = "jdoe",
                    SubmittedAt = submittedAt,
                    Status = "Completed"
                }
            }, 1));

        // Act — session 1: submit-time session sees the task.
        var session1Cookie = await LoginAsync();
        using (var session1Response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/tasks/100234", session1Cookie)))
        {
            session1Response.StatusCode.Should().Be(HttpStatusCode.OK);
            var session1Body = await session1Response.Content.ReadFromJsonAsync<TaskDetailResponse>();
            session1Body!.Status.Should().Be("Completed");
        }

        // Act — session 1's cookie is discarded (the browser was closed) and a completely independent
        // session 2 is established via a fresh login, with no dependency on session 1 at all.
        var session2Cookie = await LoginAsync();

        // Assert
        using var detailResponse = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/tasks/100234", session2Cookie));
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailBody = await detailResponse.Content.ReadFromJsonAsync<TaskDetailResponse>();
        detailBody!.TaskId.Should().Be(100234);
        detailBody.Status.Should().Be("Completed");
        detailBody.StartTime.Should().Be(startedAt, "CAS-030 §6: startTime is started_at, not submitted_at");

        using var listResponse = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/tasks", session2Cookie));
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listBody = await listResponse.Content.ReadFromJsonAsync<PagedResult<TaskListItem>>();
        listBody!.Items.Should().ContainSingle(i => i.TaskId == 100234 && i.Status == "Completed");
    }

    // ---- CAS-026 V-8 (mark-viewed scoping) superseded by CAS-030: POST /tasks/mark-viewed no longer exists;
    //      per-user acknowledge scoping is proven by Test_V3_OtherUsersTask_Returns404IdenticalToMissing above.

    // ---- Branch coverage: GET /tasks's 200/400 branches and GET /tasks/{taskId}'s 200 (Found) branch -

    [Fact]
    public async Task Test_List_ReturnsOkWithPagedTasks()
    {
        // Arrange
        SeedUser(userId: 7);
        var cookie = await LoginAsync();
        var row = new TaskQueueListRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            SubmittedByUserId = 7,
            Username = "jdoe",
            SubmittedAt = new DateTimeOffset(2026, 9, 24, 14, 5, 3, TimeSpan.Zero),
            Status = "Running"
        };
        _factory.TaskQueueRepository
            .ListAsync(7, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<TaskQueueListRow> { row }, 1));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/tasks", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedResult<TaskListItem>>();
        body!.Items.Should().ContainSingle(i => i.TaskId == 100234 && i.Status == "Running");
        body.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_List_UnrecognizedStatus_Returns400OverHttp()
    {
        // Arrange
        SeedUser(userId: 7);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/tasks?status=NotARealStatus", cookie));

        // Assert — CAS-030 V2: 400 validation-failed (supersedes CAS-026's 422).
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Test_GetDetail_ReturnsOkForOwnedTask()
    {
        // Arrange
        SeedUser(userId: 7);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository
            .GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(TaskRow(100234, "KeywordSearch", "Running", submittedByUserId: 7,
                new DateTimeOffset(2026, 9, 24, 14, 5, 3, TimeSpan.Zero)));
        _factory.UserRepository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = 7,
            Username = "jdoe",
            PasswordHash = "unused",
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = 0
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/tasks/100234", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TaskDetailResponse>();
        body!.TaskId.Should().Be(100234);
        body.SubmittedBy.Should().Be("jdoe");
        body.ResultDetail.Should().BeNull();
    }

    // ---- Standard 401 branch for all four new read/write endpoints: no session, no repository call --

    [Theory]
    [InlineData("GET", "/tasks")]
    [InlineData("GET", "/tasks/100234")]
    [InlineData("GET", "/tasks/summary")]
    public async Task Test_TaskManagerReadEndpoints_NoSession_Returns401(string method, string url)
    {
        // Act
        using var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.TaskQueueRepository.DidNotReceive().ListAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.TaskQueueRepository.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.TaskQueueRepository.DidNotReceive().GetSummaryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.UserRepository.DidNotReceive().MarkTaskManagerViewedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
