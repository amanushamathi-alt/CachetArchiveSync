using System.Reflection;
using System.Security.Claims;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

// CAS-056 (Cancel a running Keyword Search - backend) Api-side unit tests. Test names map 1:1 to the
// spec's V/SC ids; methods named Cov_* are not map items (coverage of remaining branches only).

/// <summary>
/// CAS-056 §13 "TaskManagerControllerTests": the cancel route's outcome -> status mapping,
/// action-level <c>[Authorize]</c>, and the identical 404 body (V1, V2).
/// </summary>
public sealed class TaskManagerCancelControllerTests
{
    private const string TaskNotFoundType = "https://cachet.tracecounts.internal/errors/task-not-found";
    private const string NotCancellableType = "https://cachet.tracecounts.internal/errors/task-not-cancellable";

    private readonly ITaskManagerService _service = Substitute.For<ITaskManagerService>();

    private TaskManagerController CreateSut(int? userId = 9)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<ApiBehaviorOptions>(_ => { });
        services.AddSingleton<ProblemDetailsFactory, TestProblemDetailsFactory>();

        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        }
        claims.Add(new Claim(ClaimTypes.Name, "m.alvarez"));

        return new TaskManagerController(_service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private void Returns(int taskId, CancelTaskOutcome outcome) =>
        _service.CancelAsync(taskId, 9, Arg.Any<CancellationToken>())
            .Returns(new CancelTaskResult { Outcome = outcome });

    [Fact]
    public async Task Test_V1_CancelNotOwnedAndMissingAreIndistinguishable()
    {
        // Arrange - the service reports NotFound for both "missing" and "not owned" (ownership is in the SP).
        Returns(404001, CancelTaskOutcome.NotFound);
        Returns(404002, CancelTaskOutcome.NotFound);
        _service.GetAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TaskDetailResponse?)null);
        var sut = CreateSut();

        // Act
        var missing = await sut.Cancel(404001, CancellationToken.None);
        var notOwned = await sut.Cancel(404002, CancellationToken.None);
        var getMissing = await sut.Get(404001, CancellationToken.None);

        // Assert - identical status, type, title and body; also identical to GET's 404 (CAS-030 V3).
        var a = missing.Should().BeOfType<ObjectResult>().Subject;
        var b = notOwned.Should().BeOfType<ObjectResult>().Subject;
        var g = getMissing.Should().BeOfType<ObjectResult>().Subject;
        a.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var pa = a.Value.Should().BeOfType<ProblemDetails>().Subject;
        var pb = b.Value.Should().BeOfType<ProblemDetails>().Subject;
        var pg = g.Value.Should().BeOfType<ProblemDetails>().Subject;
        pa.Type.Should().Be(TaskNotFoundType);
        (pb.Type, pb.Title, pb.Detail, pb.Status).Should().Be((pa.Type, pa.Title, pa.Detail, pa.Status));
        (pg.Type, pg.Title, pg.Detail, pg.Status).Should().Be((pa.Type, pa.Title, pa.Detail, pa.Status));
        pa.Detail.Should().BeNull("the body must not echo the task id or owner");
    }

    [Fact]
    public async Task Test_V2_CancelTerminalOrWrongTypeIs409()
    {
        // Arrange
        Returns(100234, CancelTaskOutcome.NotCancellable);
        var sut = CreateSut();

        // Act
        var result = await sut.Cancel(100234, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Which.Type.Should().Be(NotCancellableType);
    }

    [Fact]
    public async Task Cov_Cancel_Queued_Returns200Cancelled()
    {
        Returns(100234, CancelTaskOutcome.Cancelled);

        var result = await CreateSut().Cancel(100234, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeEquivalentTo(new { taskId = 100234, status = "Cancelled" });
    }

    [Fact]
    public async Task Cov_Cancel_Running_Returns202Cancelling()
    {
        Returns(100234, CancelTaskOutcome.CancelRequested);

        var result = await CreateSut().Cancel(100234, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        accepted.Value.Should().BeEquivalentTo(new { taskId = 100234, status = "Cancelling" });
    }

    [Fact]
    public async Task Cov_Cancel_NoUserIdClaim_Returns401_AndNeverCallsService()
    {
        var result = await CreateSut(userId: null).Cancel(100234, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        _service.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Cov_Cancel_UsesClaimDerivedUserId()
    {
        Returns(100234, CancelTaskOutcome.CancelRequested);

        await CreateSut(userId: 9).Cancel(100234, CancellationToken.None);

        await _service.Received(1).CancelAsync(100234, 9, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Cov_Cancel_IsPostRoute_WithActionLevelAuthorize()
    {
        var method = typeof(TaskManagerController).GetMethod(nameof(TaskManagerController.Cancel));

        var authorize = method!.GetCustomAttributes<AuthorizeAttribute>(inherit: false).Single();
        var post = method.GetCustomAttributes<HttpPostAttribute>(inherit: false).Single();

        authorize.Policy.Should().Be("CachetAuthenticated");
        post.Template.Should().Be("tasks/{taskId:int}/cancel");
    }
}

/// <summary>CAS-056 §13 "TaskManagerServiceTests": outcome pass-through and no extra audit write.</summary>
public sealed class TaskManagerCancelServiceTests
{
    [Theory]
    [InlineData(CancelTaskOutcome.Cancelled)]
    [InlineData(CancelTaskOutcome.CancelRequested)]
    [InlineData(CancelTaskOutcome.NotFound)]
    [InlineData(CancelTaskOutcome.NotCancellable)]
    public async Task Cov_CancelAsync_PassesOutcomeThrough_WithoutExtraAuditWrite(CancelTaskOutcome outcome)
    {
        // Arrange
        var repository = Substitute.For<ITaskQueueRepository>();
        var audit = Substitute.For<IAuditLogService>();
        var logger = Substitute.For<ILogger<TaskManagerService>>();
        repository.CancelAsync(100234, 9, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CancelTaskResult { Outcome = outcome });
        var sut = new TaskManagerService(repository, audit, logger);

        // Act
        var result = await sut.CancelAsync(100234, 9, CancellationToken.None);

        // Assert - the SP audits in its own transaction; the service must not write a second row.
        result.Outcome.Should().Be(outcome);
        await repository.Received(1).CancelAsync(100234, 9, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>CAS-056 §13 "SearchControllerTests" (service half): Cancelled results are 200 + partial (V11, SC-3).</summary>
public sealed class KeywordSearchCancelledResultsTests
{
    private const int TaskId = 100234;

    private readonly ITaskQueueRepository _taskQueue = Substitute.For<ITaskQueueRepository>();
    private readonly ISearchResultRepository _results = Substitute.For<ISearchResultRepository>();

    private KeywordSearchService CreateSut() =>
        new(_taskQueue, _results, Substitute.For<IAuditLogService>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:BucketName"] = "cachet-archive-prod",
                ["Search:MaxTermLength"] = "200"
            }).Build(),
            Substitute.For<ILogger<KeywordSearchService>>());

    private void GivenTask(string status, int? processed = 40, int? total = 100) =>
        _taskQueue.GetAsync(TaskId, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = TaskId,
            TaskType = "KeywordSearch",
            Status = status,
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow,
            ItemsProcessed = processed,
            ItemsTotal = total
        });

    private static SearchResultRow Row(int id, string matchType) => new()
    {
        SearchResultId = id,
        TaskQueueId = TaskId,
        FileIndexId = id,
        MatchType = matchType,
        MatchSnippet = matchType == "Content" ? "snippet" : null,
        MatchStatus = "Flagged",
        CreatedAt = DateTimeOffset.UtcNow,
        FileName = $"f{id}.pdf",
        RelativePath = $"a/f{id}.pdf",
        FileSizeBytes = 10,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Test_V11_CancelledResultsArePartialAnd200()
    {
        // Arrange - rows written before the cancel are retained.
        GivenTask("Cancelled", processed: 40, total: 100);
        _results.ListAsync(TaskId, "Filename", 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow> { Row(1, "Filename"), Row(2, "Filename") }, 2));
        _results.ListAsync(TaskId, "Content", 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow> { Row(3, "Content") }, 1));

        // Act
        var result = await CreateSut().GetResultsAsync(TaskId, 1, 50, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchResultsOutcome.Found);
        var r = result.Response!;
        r.Status.Should().Be("Cancelled");
        r.IsPartial.Should().BeTrue();
        r.ItemsProcessed.Should().Be(40);
        r.ItemsTotal.Should().Be(100);
        r.FailureReason.Should().BeNull();
        r.FilenameMatches!.Items.Should().HaveCount(2);
        r.FilenameMatches.TotalCount.Should().Be(2);
        r.ContentMatches!.Items.Should().ContainSingle(i => i.FileId == 3);

        // Completed stays non-partial (contract: IsPartial true only for Cancelled).
        GivenTask("Completed");
        var completed = (await CreateSut().GetResultsAsync(TaskId, 1, 50, CancellationToken.None)).Response!;
        completed.IsPartial.Should().BeFalse();
        completed.ItemsProcessed.Should().BeNull();
        completed.ItemsTotal.Should().BeNull();
    }

    [Fact]
    public async Task Test_SC3_PartialAndEmptyResults()
    {
        // Arrange - cancelled before any match: 200 with empty pages (not 404/409/Failed).
        GivenTask("Cancelled", processed: 0, total: 100);
        _results.ListAsync(TaskId, Arg.Any<string>(), 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow>(), 0));

        // Act
        var result = await CreateSut().GetResultsAsync(TaskId, 1, 50, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchResultsOutcome.Found);
        result.Response!.Status.Should().Be("Cancelled");
        result.Response.IsPartial.Should().BeTrue();
        result.Response.FilenameMatches!.Items.Should().BeEmpty();
        result.Response.ContentMatches!.Items.Should().BeEmpty();
        result.Response.FilenameMatches.TotalCount.Should().Be(0);
        result.Response.ContentMatches.TotalCount.Should().Be(0);
    }
}

/// <summary>CAS-056 §13 "SearchResultBrowseServiceTests" (V12) - the browse/result-ids status gate.</summary>
public sealed class SearchResultBrowseCancelGateTests
{
    private const int UserId = 7;
    private const int TaskId = 100234;

    private readonly ITaskQueueRepository _taskQueue = Substitute.For<ITaskQueueRepository>();
    private readonly ISearchResultBrowseRepository _repository = Substitute.For<ISearchResultBrowseRepository>();

    private SearchResultBrowseService Build(string status)
    {
        var prefix = Substitute.For<IB2PathPrefixProvider>();
        prefix.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("D/Shares/Operations");
        _taskQueue.GetAsync(TaskId, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = TaskId,
            TaskType = "KeywordSearch",
            Status = status,
            SubmittedByUserId = UserId,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        _repository.BrowseAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultBrowsePage
            {
                Folders = Array.Empty<FileBrowseFolderRow>(),
                Files = Array.Empty<SearchResultBrowseFileRow>(),
                TotalFolders = 0,
                TotalFiles = 0
            });
        _repository.ListIdsByFolderAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 5521 });

        return new SearchResultBrowseService(
            _taskQueue, _repository, prefix,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = "Cachet-Server-C" }).Build(),
            Substitute.For<ILogger<SearchResultBrowseService>>());
    }

    [Fact]
    public async Task Test_V12_BrowseAcceptsCancelled()
    {
        // Completed and Cancelled are accepted for both browse and result-ids.
        foreach (var accepted in new[] { "Completed", "Cancelled" })
        {
            var sut = Build(accepted);
            var browse = await sut.BrowseSearchResultsAsync(TaskId, UserId, "", false, CancellationToken.None);
            var ids = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "", CancellationToken.None);

            browse.Outcome.Should().Be(SearchResultBrowseOutcome.Success, accepted);
            ids.Outcome.Should().Be(SearchResultBrowseOutcome.Success, accepted);
        }

        // Queued / Running / Failed are unchanged: TaskNotComplete, nothing queried.
        foreach (var rejected in new[] { "Queued", "Running", "Failed" })
        {
            var sut = Build(rejected);
            _repository.ClearReceivedCalls();
            var browse = await sut.BrowseSearchResultsAsync(TaskId, UserId, "", false, CancellationToken.None);
            var ids = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "", CancellationToken.None);

            browse.Outcome.Should().Be(SearchResultBrowseOutcome.TaskNotComplete, rejected);
            ids.Outcome.Should().Be(SearchResultBrowseOutcome.TaskNotComplete, rejected);
            _repository.ReceivedCalls().Should().BeEmpty(rejected);
        }
    }
}

/// <summary>CAS-056 SC-8 / Q6 - QuarantineService has no status gate, so a Cancelled task's partial rows can be quarantined.</summary>
public sealed class QuarantineFromCancelledResultsTests
{
    [Fact]
    public async Task Test_SC8_QuarantineFromCancelledResults()
    {
        // Arrange
        var taskQueue = Substitute.For<ITaskQueueRepository>();
        var searchResults = Substitute.For<ISearchResultRepository>();
        var quarantine = Substitute.For<IQuarantineEntryRepository>();
        var audit = Substitute.For<IAuditLogService>();
        taskQueue.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Cancelled",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        searchResults.GetAsync(100501, Arg.Any<CancellationToken>()).Returns(new SearchResultRow
        {
            SearchResultId = 100501,
            TaskQueueId = 100234,
            FileIndexId = 1,
            MatchType = "Filename",
            MatchStatus = "Flagged",
            CreatedAt = DateTimeOffset.UtcNow,
            FileName = "agreement-004.pdf",
            RelativePath = "contracts/2019/agreement-004.pdf",
            FileSizeBytes = 1,
            ModifiedAt = DateTimeOffset.UtcNow
        });
        quarantine.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), 100501, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.Added, QuarantineEntryId = 100801 });
        var sut = new QuarantineService(taskQueue, searchResults, quarantine, audit, Substitute.For<ILogger<QuarantineService>>());

        // Act
        var result = await sut.AddAsync(new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } }, 1, CancellationToken.None);

        // Assert - same as Completed.
        result.Outcome.Should().Be(QuarantineAddOutcome.Added);
        result.Response!.Items.Should().ContainSingle(i => i.FileId == 100801);
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineAdd && e.TaskQueueId == 100234),
            Arg.Any<CancellationToken>());
    }
}

/// <summary>CAS-056 SC-9 - no B2 key, session token, term, path or file name in any new log line or error body.</summary>
public sealed class CancelLogHygieneTests
{
    private const string SensitiveTerm = "acme-corp-merger-confidential-code-7734";
    private const string FakeB2Key = "K001FakeApplicationKeyValueNeverLog";
    private const string FakeSessionToken = "CachetAuth=fakeSessionCookieValueNeverLog";

    private static List<string> Messages<T>(ILogger<T> logger)
    {
        var messages = new List<string>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    private static void AssertClean(IEnumerable<string> texts) =>
        texts.Should().NotContain(m =>
            m.Contains(SensitiveTerm, StringComparison.Ordinal) ||
            m.Contains(FakeB2Key, StringComparison.Ordinal) ||
            m.Contains(FakeSessionToken, StringComparison.Ordinal) ||
            m.Contains("agreement-004", StringComparison.Ordinal));

    [Fact]
    public async Task Test_SC9_LogHygiene()
    {
        // Arrange - a task whose stored payload / results carry sensitive values.
        var repository = Substitute.For<ITaskQueueRepository>();
        var logger = Substitute.For<ILogger<TaskManagerService>>();
        var sut = new TaskManagerService(repository, Substitute.For<IAuditLogService>(), logger);

        // Act - every cancel outcome.
        foreach (var outcome in Enum.GetValues<CancelTaskOutcome>())
        {
            repository.CancelAsync(100234, 9, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new CancelTaskResult { Outcome = outcome });
            await sut.CancelAsync(100234, 9, CancellationToken.None);
        }

        // The cancelled-results read path (logger of KeywordSearchService).
        var searchTaskQueue = Substitute.For<ITaskQueueRepository>();
        var searchResults = Substitute.For<ISearchResultRepository>();
        var searchLogger = Substitute.For<ILogger<KeywordSearchService>>();
        searchTaskQueue.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Cancelled",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = $"{{\"term\":\"{SensitiveTerm}\"}}"
        });
        searchResults.ListAsync(100234, Arg.Any<string>(), 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow>(), 0));
        var searchSut = new KeywordSearchService(
            searchTaskQueue, searchResults, Substitute.For<IAuditLogService>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:BucketName"] = "cachet-archive-prod",
                ["Search:MaxTermLength"] = "200"
            }).Build(),
            searchLogger);
        await searchSut.GetResultsAsync(100234, 1, 50, CancellationToken.None);

        // The cancel 404/409 bodies are fixed strings.
        var controllerService = Substitute.For<ITaskManagerService>();
        var bodies = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<ApiBehaviorOptions>(_ => { });
        services.AddSingleton<ProblemDetailsFactory, TestProblemDetailsFactory>();
        var controller = new TaskManagerController(controllerService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "9") }, "Test"))
                }
            }
        };
        foreach (var outcome in new[] { CancelTaskOutcome.NotFound, CancelTaskOutcome.NotCancellable })
        {
            controllerService.CancelAsync(100234, 9, Arg.Any<CancellationToken>())
                .Returns(new CancelTaskResult { Outcome = outcome });
            var result = (ObjectResult)await controller.Cancel(100234, CancellationToken.None);
            var problem = (ProblemDetails)result.Value!;
            bodies.Add($"{problem.Type}|{problem.Title}|{problem.Detail}");
        }

        // Assert
        AssertClean(Messages(logger));
        AssertClean(Messages(searchLogger));
        AssertClean(bodies);
        Messages(logger).Should().NotBeEmpty("the cancel path logs the task id/outcome");
    }
}
