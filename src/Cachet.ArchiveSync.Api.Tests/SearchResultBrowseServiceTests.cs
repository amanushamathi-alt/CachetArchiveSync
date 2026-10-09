using System.Security.Claims;
using System.Text.Json;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-049 §13 - <see cref="SearchResultBrowseService"/> unit tests (V1, V2, V6) plus the controller-level
/// V3 guard and outcome mapping of <see cref="SearchController.Browse"/> / <see cref="SearchController.GetResultIds"/>.
/// Repositories and the prefix provider are substituted; nothing here touches SQL Server. Methods named
/// <c>Cov_*</c> are not map items: they exist only to cover the changed files' remaining branches (>= 85%).
/// </summary>
public sealed class SearchResultBrowseServiceTests
{
    private const string Bucket = "Cachet-Server-C";
    private const string Prefix = "D/Shares/Operations";
    private const int UserId = 7;
    private const int TaskId = 100234;
    private const string ErrorBase = "https://cachet.tracecounts.internal/errors/";

    private readonly ITaskQueueRepository _taskQueue = Substitute.For<ITaskQueueRepository>();
    private readonly ISearchResultBrowseRepository _repository = Substitute.For<ISearchResultBrowseRepository>();
    private readonly IB2PathPrefixProvider _prefixProvider = Substitute.For<IB2PathPrefixProvider>();
    private readonly CapturingLogger<SearchResultBrowseService> _logger = new();

    public SearchResultBrowseServiceTests()
    {
        _prefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Prefix);
        ReturnsTask(MakeTask("KeywordSearch", "Completed", UserId));
        _repository.BrowseAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(NonEmptyPage());
        _repository.ListIdsByFolderAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 5521, 5522 });
    }

    private SearchResultBrowseService Build(string? bucketName = Bucket) =>
        new(_taskQueue, _repository, _prefixProvider,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = bucketName })
                .Build(),
            _logger);

    private static TaskQueueRow MakeTask(string type, string status, int owner) => new()
    {
        TaskQueueId = TaskId,
        TaskType = type,
        Status = status,
        SubmittedByUserId = owner,
        SubmittedAt = DateTimeOffset.UtcNow
    };

    private void ReturnsTask(TaskQueueRow? row) =>
        _taskQueue.GetAsync(TaskId, Arg.Any<CancellationToken>()).Returns(row);

    private static SearchResultBrowsePage NonEmptyPage() => new()
    {
        Folders = new[] { new FileBrowseFolderRow("A", new DateTimeOffset(2024, 10, 29, 19, 36, 6, TimeSpan.Zero), true) },
        Files = new[]
        {
            new SearchResultBrowseFileRow(100245, 5521, "HRButler 141104.01", ".01", 1946,
                new DateTimeOffset(2014, 11, 4, 21, 49, 0, TimeSpan.Zero), "Flagged"),
            new SearchResultBrowseFileRow(100246, 5522, "noext", null, 5, DateTimeOffset.UnixEpoch, "Needs Review")
        },
        TotalFolders = 1,
        TotalFiles = 2
    };

    private Task<SearchResultBrowseResult> BrowseAsync(
        SearchResultBrowseService sut, string? folder = "", bool foldersOnly = false) =>
        sut.BrowseSearchResultsAsync(TaskId, UserId, folder, foldersOnly, CancellationToken.None);

    private async Task AssertNothingQueriedAsync()
    {
        await _repository.DidNotReceiveWithAnyArgs().BrowseAsync(default, default!, default!, default, default);
        await _repository.DidNotReceiveWithAnyArgs().ListIdsByFolderAsync(default, default!, default!, default, default);
    }

    // ---- V1 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V1_ForeignOrMissingTaskReturns404()
    {
        var sut = Build();

        // missing
        ReturnsTask(null);
        var missing = await BrowseAsync(sut);
        var missingIds = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "", CancellationToken.None);

        // another user's task
        ReturnsTask(MakeTask("KeywordSearch", "Completed", UserId + 1));
        var foreign = await BrowseAsync(sut);
        var foreignIds = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "", CancellationToken.None);

        // not a search task (Sync tasks have no search_result rows)
        ReturnsTask(MakeTask("SyncCopy", "Completed", UserId));
        var wrongType = await BrowseAsync(sut);

        new[] { missing.Outcome, foreign.Outcome, wrongType.Outcome, missingIds.Outcome, foreignIds.Outcome }
            .Should().OnlyContain(o => o == SearchResultBrowseOutcome.TaskNotFound,
                "missing, not-owned and wrong-type tasks are indistinguishable");
        missing.Response.Should().BeNull();
        foreignIds.Response.Should().BeNull();
        await AssertNothingQueriedAsync();
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("Failed")]
    public async Task Cov_NonCompletedTaskReturnsTaskNotComplete(string status)
    {
        ReturnsTask(MakeTask("KeywordSearch", status, UserId));
        var sut = Build();

        var browse = await BrowseAsync(sut);
        var ids = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "", CancellationToken.None);

        browse.Outcome.Should().Be(SearchResultBrowseOutcome.TaskNotComplete);
        ids.Outcome.Should().Be(SearchResultBrowseOutcome.TaskNotComplete);
        await AssertNothingQueriedAsync();
    }

    // ---- V2 ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Accounts")]
    [InlineData("Accounts/")]
    [InlineData("Accounts//2026")]
    [InlineData("Accounts\\2026")]
    [InlineData("Accounts/../Secret")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("Accounts/./2026")]
    [InlineData("Acc\u0001ounts")]
    [InlineData("Acc\nounts")]
    public async Task Test_V2_MalformedOrTraversalFolderRejected(string folder)
    {
        var sut = Build();

        var browse = await BrowseAsync(sut, folder);
        var ids = await sut.GetResultIdsByFolderAsync(TaskId, UserId, folder, CancellationToken.None);

        browse.Outcome.Should().Be(SearchResultBrowseOutcome.ValidationFailed);
        browse.Response.Should().BeNull();
        ids.Outcome.Should().Be(SearchResultBrowseOutcome.ValidationFailed);
        await AssertNothingQueriedAsync();
        await _taskQueue.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    public async Task Test_V2_MalformedOrTraversalFolderRejected_OverMaxLength()
    {
        var sut = Build();

        var tooLong = await BrowseAsync(sut, new string('a', 1001));
        // Within 1000 on its own, but the composed key path (prefix + "/" + folder) would exceed NVARCHAR(1000).
        var composedTooLong = await BrowseAsync(sut, new string('a', 1000 - Prefix.Length));
        var composedTooLongIds = await sut.GetResultIdsByFolderAsync(
            TaskId, UserId, new string('a', 1000 - Prefix.Length), CancellationToken.None);
        var atLimit = await BrowseAsync(sut, new string('a', 1000 - Prefix.Length - 1));

        tooLong.Outcome.Should().Be(SearchResultBrowseOutcome.ValidationFailed);
        composedTooLong.Outcome.Should().Be(SearchResultBrowseOutcome.ValidationFailed);
        composedTooLongIds.Outcome.Should().Be(SearchResultBrowseOutcome.ValidationFailed);
        atLimit.Outcome.Should().Be(SearchResultBrowseOutcome.Success);
        await _repository.Received(1).BrowseAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ---- V6 ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V6_DatabaseFailureReturnsGeneric500()
    {
        const string secretFolder = "Hidden Folder";
        var sqlText = "SELECT secret FROM dbo.file_index WHERE relative_path = 'D/Shares/Operations/Hidden Folder' (Timeout expired)";
        _repository.BrowseAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(sqlText));
        _repository.ListIdsByFolderAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(sqlText));
        var sut = Build();

        var browse = await BrowseAsync(sut, secretFolder);
        var ids = await sut.GetResultIdsByFolderAsync(TaskId, UserId, secretFolder, CancellationToken.None);

        browse.Outcome.Should().Be(SearchResultBrowseOutcome.BrowseFailed);
        browse.Response.Should().BeNull();
        ids.Outcome.Should().Be(SearchResultBrowseOutcome.BrowseFailed);
        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(2, "one Warning per failed call");
        foreach (var entry in _logger.Entries)
        {
            entry.Message.Should().NotContain(secretFolder, "the folder value is never logged (§11)");
            entry.Message.Should().NotContain("SELECT").And.NotContain("Timeout expired", "the exception message is never logged");
        }
        warnings.Should().OnlyContain(e => e.Message.Contains(nameof(InvalidOperationException)));
    }

    [Fact]
    public async Task Cov_ClientAbortSurfacedAsDatabaseExceptionIsCancellationNotFailure()
    {
        using var cts = new CancellationTokenSource();
        _repository.BrowseAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<SearchResultBrowsePage>>(_ =>
            {
                cts.Cancel();
                throw new InvalidOperationException("Operation cancelled by user.");
            });
        _repository.ListIdsByFolderAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<int>>>(_ => throw new InvalidOperationException("cancelled"));
        var sut = Build();

        var browse = async () => await sut.BrowseSearchResultsAsync(TaskId, UserId, "", false, cts.Token);
        await browse.Should().ThrowAsync<OperationCanceledException>();

        var ids = async () => await sut.GetResultIdsByFolderAsync(TaskId, UserId, "", cts.Token);
        await ids.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- coverage of the remaining service branches ---------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Cov_BucketNotConfigured(string? bucketName)
    {
        var sut = Build(bucketName);

        var browse = await BrowseAsync(sut);
        var ids = await sut.GetResultIdsByFolderAsync(TaskId, UserId, null, CancellationToken.None);

        browse.Outcome.Should().Be(SearchResultBrowseOutcome.BucketNotConfigured);
        ids.Outcome.Should().Be(SearchResultBrowseOutcome.BucketNotConfigured);
        await AssertNothingQueriedAsync();
    }

    [Fact]
    public async Task Cov_MappingMissing()
    {
        _prefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var sut = Build();

        (await BrowseAsync(sut)).Outcome.Should().Be(SearchResultBrowseOutcome.MappingMissing);
        (await sut.GetResultIdsByFolderAsync(TaskId, UserId, "", CancellationToken.None)).Outcome
            .Should().Be(SearchResultBrowseOutcome.MappingMissing);
        await AssertNothingQueriedAsync();
    }

    [Fact]
    public async Task Cov_SuccessMapsFoldersFilesBreadcrumbAndHidesPrefix()
    {
        var sut = Build();

        var root = (await BrowseAsync(sut, "")).Response!;
        var nested = (await BrowseAsync(sut, "A/B")).Response!;
        await _repository.Received(1).BrowseAsync(TaskId, Bucket, Prefix, false, Arg.Any<CancellationToken>());
        await _repository.Received(1).BrowseAsync(TaskId, Bucket, Prefix + "/A/B", false, Arg.Any<CancellationToken>());

        root.RootName.Should().Be("Operations");
        root.Parent.Should().BeNull();
        root.Breadcrumb.Should().BeEmpty();
        root.Folders.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new BrowseFolderItem { Name = "A", Path = "A", LatestModifiedAt = new DateTimeOffset(2024, 10, 29, 19, 36, 6, TimeSpan.Zero), HasSubfolders = true });
        root.TotalFolders.Should().Be(1);
        root.TotalFiles.Should().Be(2);
        root.Files[0].SearchResultId.Should().Be(5521);
        root.Files[0].MatchStatus.Should().Be("Flagged");
        root.Files[0].Extension.Should().Be("01", "leading dot trimmed, lower-cased");
        root.Files[1].Extension.Should().BeEmpty();

        nested.Folder.Should().Be("A/B");
        nested.Parent.Should().Be("A");
        nested.Breadcrumb.Select(b => (b.Name, b.Path)).Should().Equal(("A", "A"), ("B", "A/B"));
        nested.Folders.Single().Path.Should().Be("A/B/A");

        foreach (var response in new[] { root, nested })
        {
            var json = JsonSerializer.Serialize(response);
            json.Should().NotContain("D/Shares").And.NotContain(Prefix);
        }
    }

    [Fact]
    public async Task Cov_V4FolderWithNoMatchesIsFolderNotFound_LeafWithFoldersOnlyIsNot()
    {
        var empty = new SearchResultBrowsePage
        {
            Folders = Array.Empty<FileBrowseFolderRow>(),
            Files = Array.Empty<SearchResultBrowseFileRow>(),
            TotalFolders = 0,
            TotalFiles = 0
        };
        var leaf = empty with
        {
            Files = new[] { new SearchResultBrowseFileRow(1, 2, "a.txt", "txt", 1, DateTimeOffset.UnixEpoch, "Flagged") },
            TotalFiles = 1
        };
        _repository.BrowseAsync(TaskId, Bucket, Prefix + "/Leaf", true, Arg.Any<CancellationToken>()).Returns(empty);
        _repository.BrowseAsync(TaskId, Bucket, Prefix + "/Leaf", false, Arg.Any<CancellationToken>()).Returns(leaf);
        _repository.BrowseAsync(TaskId, Bucket, Prefix + "/Nope", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(empty);
        var sut = Build();

        var leafResult = await BrowseAsync(sut, "Leaf", foldersOnly: true);
        var unknownFoldersOnly = await BrowseAsync(sut, "Nope", foldersOnly: true);
        var unknownWithFiles = await BrowseAsync(sut, "Nope", foldersOnly: false);

        leafResult.Outcome.Should().Be(SearchResultBrowseOutcome.Success);
        leafResult.Response!.Folders.Should().BeEmpty();
        unknownFoldersOnly.Outcome.Should().Be(SearchResultBrowseOutcome.FolderNotFound);
        unknownWithFiles.Outcome.Should().Be(SearchResultBrowseOutcome.FolderNotFound);

        // An empty result at the root is 200 with empty lists, never 404 (§8).
        _repository.BrowseAsync(TaskId, Bucket, Prefix, false, Arg.Any<CancellationToken>()).Returns(empty);
        var root = await BrowseAsync(sut, "");
        root.Outcome.Should().Be(SearchResultBrowseOutcome.Success);
        root.Response!.Folders.Should().BeEmpty();
        root.Response.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Cov_ResultIdsSuccessCapAndFolderNotFound()
    {
        var sut = Build();
        var limit = ISearchResultBrowseService.ResultIdsLimit;

        var ok = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "A", CancellationToken.None);
        ok.Outcome.Should().Be(SearchResultBrowseOutcome.Success);
        ok.Response!.SearchResultIds.Should().Equal(5521, 5522);
        ok.Response.Count.Should().Be(2);
        await _repository.Received(1).ListIdsByFolderAsync(TaskId, Bucket, Prefix + "/A", limit + 1, Arg.Any<CancellationToken>());

        _repository.ListIdsByFolderAsync(TaskId, Bucket, Prefix + "/Exactly", limit + 1, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, limit).ToList());
        _repository.ListIdsByFolderAsync(TaskId, Bucket, Prefix + "/Over", limit + 1, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, limit + 1).ToList());
        _repository.ListIdsByFolderAsync(TaskId, Bucket, Prefix + "/Empty", limit + 1, Arg.Any<CancellationToken>())
            .Returns(new List<int>());
        _repository.ListIdsByFolderAsync(TaskId, Bucket, Prefix, limit + 1, Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        var exactly = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "Exactly", CancellationToken.None);
        var over = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "Over", CancellationToken.None);
        var empty = await sut.GetResultIdsByFolderAsync(TaskId, UserId, "Empty", CancellationToken.None);
        var emptyRoot = await sut.GetResultIdsByFolderAsync(TaskId, UserId, null, CancellationToken.None);

        exactly.Outcome.Should().Be(SearchResultBrowseOutcome.Success);
        exactly.Response!.Count.Should().Be(limit);
        over.Outcome.Should().Be(SearchResultBrowseOutcome.SelectionTooLarge);
        over.Response.Should().BeNull("no ids are returned above the cap");
        empty.Outcome.Should().Be(SearchResultBrowseOutcome.FolderNotFound);
        emptyRoot.Outcome.Should().Be(SearchResultBrowseOutcome.Success, "an empty whole-task selection is 200 with no ids");
        emptyRoot.Response!.Count.Should().Be(0);
    }

    // ---- controller (V3 + outcome mapping) -------------------------------------------------------------

    private static SearchController BuildController(int? userId = UserId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<ApiBehaviorOptions>(_ => { });
        services.AddSingleton<ProblemDetailsFactory, TestProblemDetailsFactory>();
        var identity = userId is null
            ? new ClaimsIdentity("Test")
            : new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test");
        return new SearchController(Substitute.For<IKeywordSearchService>(), Substitute.For<IQuarantineService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services.BuildServiceProvider(),
                    User = new ClaimsPrincipal(identity)
                }
            }
        };
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("yes please")]
    public async Task Test_V3_NonBooleanFoldersOnlyRejected(string value)
    {
        var service = Substitute.For<ISearchResultBrowseService>();
        var sut = BuildController();

        var result = await sut.Browse(TaskId, service, "A", value, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        ((ProblemDetails)problem.Value!).Type.Should().Be(ErrorBase + "validation-failed");
        await service.DidNotReceiveWithAnyArgs().BrowseSearchResultsAsync(default, default, default, default, default);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public async Task Cov_ControllerParsesFoldersOnlyAndReturns200(string? value, bool expected)
    {
        var service = Substitute.For<ISearchResultBrowseService>();
        var response = new FolderBrowseResponse
        {
            RootName = "Operations",
            Folder = "",
            Parent = null,
            Breadcrumb = Array.Empty<BreadcrumbItem>(),
            Folders = Array.Empty<BrowseFolderItem>(),
            Files = Array.Empty<BrowseFileItem>(),
            TotalFolders = 0,
            TotalFiles = 0
        };
        service.BrowseSearchResultsAsync(TaskId, UserId, "A", expected, Arg.Any<CancellationToken>())
            .Returns(new SearchResultBrowseResult { Outcome = SearchResultBrowseOutcome.Success, Response = response });

        var result = await BuildController().Browse(TaskId, service, "A", value, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task Cov_ControllerWithoutUserClaimReturns401()
    {
        var service = Substitute.For<ISearchResultBrowseService>();
        var sut = BuildController(userId: null);

        (await sut.Browse(TaskId, service, "", null, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await sut.GetResultIds(TaskId, service, "", CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        await service.DidNotReceiveWithAnyArgs().BrowseSearchResultsAsync(default, default, default, default, default);
        await service.DidNotReceiveWithAnyArgs().GetResultIdsByFolderAsync(default, default, default, default);
    }

    [Theory]
    [InlineData(SearchResultBrowseOutcome.ValidationFailed, 400, "validation-failed")]
    [InlineData(SearchResultBrowseOutcome.TaskNotFound, 404, "task-not-found")]
    [InlineData(SearchResultBrowseOutcome.TaskNotComplete, 409, "task-not-complete")]
    [InlineData(SearchResultBrowseOutcome.FolderNotFound, 404, "folder-not-found")]
    [InlineData(SearchResultBrowseOutcome.SelectionTooLarge, 400, "selection-too-large")]
    [InlineData(SearchResultBrowseOutcome.BucketNotConfigured, 500, "bucket-not-configured")]
    [InlineData(SearchResultBrowseOutcome.MappingMissing, 500, "mirror-mapping-missing")]
    [InlineData(SearchResultBrowseOutcome.BrowseFailed, 500, "browse-failed")]
    public async Task Cov_ControllerMapsEveryOutcomeToStatusAndProblemType(
        SearchResultBrowseOutcome outcome, int status, string slug)
    {
        var service = Substitute.For<ISearchResultBrowseService>();
        service.BrowseSearchResultsAsync(TaskId, UserId, Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultBrowseResult { Outcome = outcome });
        service.GetResultIdsByFolderAsync(TaskId, UserId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultIdsResult { Outcome = outcome });
        var sut = BuildController();

        var browse = await sut.Browse(TaskId, service, "A", null, CancellationToken.None);
        var ids = await sut.GetResultIds(TaskId, service, "A", CancellationToken.None);

        foreach (var result in new[] { browse, ids })
        {
            var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
            objectResult.StatusCode.Should().Be(status);
            ((ProblemDetails)objectResult.Value!).Type.Should().Be(ErrorBase + slug);
            if (outcome == SearchResultBrowseOutcome.SelectionTooLarge)
            {
                ((ProblemDetails)objectResult.Value!).Extensions["limit"].Should().Be(1000);
            }
        }
    }

    [Fact]
    public async Task Cov_ControllerResultIdsSuccessReturns200WithBody()
    {
        var service = Substitute.For<ISearchResultBrowseService>();
        var body = new SearchResultIdsResponse { SearchResultIds = new[] { 5521, 5522 }, Count = 2 };
        service.GetResultIdsByFolderAsync(TaskId, UserId, "A", Arg.Any<CancellationToken>())
            .Returns(new SearchResultIdsResult { Outcome = SearchResultBrowseOutcome.Success, Response = body });

        var result = await BuildController().GetResultIds(TaskId, service, "A", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(body);
    }

    // ---- BrowseFileItem JSON: new fields omitted for /files/browse ---------------------------------------

    [Fact]
    public void Cov_BrowseFileItemJsonOmitsSearchFieldsWhenNull_IncludesThemWhenSet()
    {
        var plain = new BrowseFileItem { FileIndexId = 1, Name = "a", Extension = "txt", SizeBytes = 1, ModifiedAt = DateTimeOffset.UnixEpoch };
        var match = plain with { SearchResultId = 5521, MatchStatus = "Flagged" };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var plainJson = JsonSerializer.Serialize(plain, options);
        var matchJson = JsonSerializer.Serialize(match, options);

        plainJson.Should().NotContain("searchResultId").And.NotContain("matchStatus");
        matchJson.Should().Contain("\"searchResultId\":5521").And.Contain("\"matchStatus\":\"Flagged\"");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += " | " + exception;
            }

            Entries.Add((logLevel, message));
        }
    }
}
