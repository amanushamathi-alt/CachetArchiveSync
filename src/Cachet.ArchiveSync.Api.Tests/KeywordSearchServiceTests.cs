using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="KeywordSearchService"/> (CAS-006 §6/§6.2). Plays the role the spec's
/// §13 Test Plan assigns to "SearchRequestValidatorTests" for V1–V3 — no separate validator class
/// exists in the actual implementation (validation is inline in <see cref="KeywordSearchService.SubmitAsync"/>),
/// so those tests live here against the real service with its repositories mocked. Also proves
/// SC-1/SC-2 at the service layer (the full-HTTP-round-trip proof of the same behavior lives in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.SearchControllerTests</c>) plus every
/// <see cref="KeywordSearchService.GetResultsAsync"/> branch (§7 V4/V5, §6.2's Completed/Failed
/// shapes) for line-coverage completeness.
/// </summary>
public sealed class KeywordSearchServiceTests
{
    private const string ConfiguredBucket = "cachet-archive-prod";

    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<KeywordSearchService> Logger { get; } = Substitute.For<ILogger<KeywordSearchService>>();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:BucketName"] = ConfiguredBucket,
                ["Search:MaxTermLength"] = "200"
            })
            .Build();

        public KeywordSearchService CreateSut() =>
            new(TaskQueueRepository, SearchResultRepository, AuditLogService, Configuration, Logger);
    }

    private static SearchRequest ValidRequest(string term = "confidential settlement") => new()
    {
        Term = term,
        Bucket = ConfiguredBucket,
        Extension = "All"
    };

    // ---- V1: term required, <= 200 chars after trim -------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Test_V1_SearchTermRequiredAndBounded(string blankTerm)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest(blankTerm);

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V1_SearchTermOverMaxLength_Returns422()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest(new string('a', 201));

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        result.ValidationError.Should().Contain("200");
    }

    [Fact]
    public async Task Test_V1_SearchTermAtMaxLength_IsAccepted()
    {
        // Arrange — boundary: exactly 200 characters is valid, not rejected.
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100234);
        var sut = fixture.CreateSut();
        var request = ValidRequest(new string('a', 200));

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
    }

    // ---- V2: bucket must equal the configured B2:BucketName exactly -----------------------------

    [Theory]
    [InlineData("some-other-bucket")]
    [InlineData("")]
    [InlineData("Cachet-Archive-Prod")] // case-sensitive per V2's "exactly"
    public async Task Test_V2_SearchBucketMustMatchConfiguredBucket(string wrongBucket)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest() with { Bucket = wrongBucket };

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- V3: extension must be one of the allowed values ----------------------------------------

    [Theory]
    [InlineData("PPT")]
    [InlineData("")]
    [InlineData("all")] // wrong case — allowed set is exact-string, ordinal
    public async Task Test_V3_SearchExtensionMustBeAllowedValue(string invalidExtension)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest() with { Extension = invalidExtension };

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("All")]
    [InlineData("PDF")]
    [InlineData("DOC")]
    [InlineData("DOCX")]
    [InlineData("XLS")]
    [InlineData("XLSX")]
    [InlineData("TXT")]
    public async Task Test_V3_SearchExtensionAllowedValues_AreAccepted(string validExtension)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100234);
        var sut = fixture.CreateSut();
        var request = ValidRequest() with { Extension = validExtension };

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
    }

    // ---- SC-1: valid submission queues a task, audits before the response is built --------------

    [Fact]
    public async Task Test_SC1_SearchSubmissionQueuesTaskAndAuditsBeforeResponse()
    {
        // Arrange
        var fixture = new Fixture();
        const int expectedTaskId = 100234;
        var callOrder = new List<string>();
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "KeywordSearch", 7, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(expectedTaskId)
            .AndDoes(_ => callOrder.Add("insert-task"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitAsync(ValidRequest(), submittedByUserId: 7, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
        result.Response.Should().NotBeNull();
        result.Response!.TaskId.Should().Be(expectedTaskId);
        result.Response.Type.Should().Be("KeywordSearch");
        result.Response.Status.Should().Be("Queued");

        await fixture.TaskQueueRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "KeywordSearch", 7, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.KeywordSearchSubmitted &&
                e.TabModule == "Keyword Search" &&
                e.TaskQueueId == expectedTaskId &&
                e.UserId == 7 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());

        // §12/CLAUDE.md: audit write happens before the response is returned to the caller.
        callOrder.Should().Equal("insert-task", "audit", "response-returned");
    }

    // ---- SC-2: invalid request never inserts a task_queue row -------------------------------------

    [Fact]
    public async Task Test_SC2_InvalidSearchRequestInsertsNoTask()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest(term: "");

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- GetResultsAsync branch coverage (V4/V5, §6.2 Completed/Failed shapes) --------------------

    [Fact]
    public async Task Test_GetResultsAsync_UnknownTaskId_ReturnsTaskNotFound()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(999, Arg.Any<CancellationToken>()).Returns((TaskQueueRow?)null);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(999, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchResultsOutcome.TaskNotFound);
    }

    [Fact]
    public async Task Test_GetResultsAsync_WrongTaskType_ReturnsTaskNotFound()
    {
        // Arrange — a real task_queue_id that belongs to a different task_type (§7 V4).
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "ContentScan",
            Status = "Completed",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(100234, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchResultsOutcome.TaskNotFound);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task Test_GetResultsAsync_QueuedOrRunning_ReturnsTaskNotComplete(string status)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = status,
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(100234, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchResultsOutcome.TaskNotComplete);
    }

    [Fact]
    public async Task Test_GetResultsAsync_Completed_ReturnsPaginatedPanels()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Completed",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        var filenameRow = new SearchResultRow
        {
            SearchResultId = 100501,
            TaskQueueId = 100234,
            FileIndexId = 1,
            MatchType = "Filename",
            MatchStatus = "Flagged",
            CreatedAt = DateTimeOffset.UtcNow,
            FileName = "agreement-004.pdf",
            RelativePath = "contracts/2019/agreement-004.pdf",
            FileSizeBytes = 482113,
            ModifiedAt = new DateTimeOffset(2019, 6, 12, 0, 0, 0, TimeSpan.Zero)
        };
        var contentRow = new SearchResultRow
        {
            SearchResultId = 100502,
            TaskQueueId = 100234,
            FileIndexId = 2,
            MatchType = "Content",
            MatchSnippet = "...regarding the confidential settlement reached in...",
            MatchStatus = "Needs Review",
            CreatedAt = DateTimeOffset.UtcNow,
            FileName = "meeting-notes-q3.docx",
            RelativePath = "internal/notes/meeting-notes-q3.docx",
            FileSizeBytes = 12345,
            ModifiedAt = new DateTimeOffset(2021, 2, 1, 0, 0, 0, TimeSpan.Zero)
        };
        fixture.SearchResultRepository.ListAsync(100234, "Filename", 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow> { filenameRow }, 1));
        fixture.SearchResultRepository.ListAsync(100234, "Content", 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow> { contentRow }, 1));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(100234, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchResultsOutcome.Found);
        result.Response!.Status.Should().Be("Completed");
        result.Response.FilenameMatches!.Items.Should().ContainSingle(i => i.FileId == 100501 && i.Name == "agreement-004.pdf");
        result.Response.FilenameMatches.TotalCount.Should().Be(1);
        result.Response.ContentMatches!.Items.Should().ContainSingle(i => i.FileId == 100502 && i.Snippet.Contains("confidential settlement"));
        result.Response.ContentMatches.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_GetResultsAsync_Failed_ReturnsFailureReason()
    {
        // Arrange
        var fixture = new Fixture();
        var resultDetail = System.Text.Json.JsonSerializer.Serialize(new KeywordSearchResultSummaryDto { FailureReason = "B2 cross-check call failed." });
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Failed",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow,
            ResultDetail = resultDetail
        });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(100234, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchResultsOutcome.Found);
        result.Response!.Status.Should().Be("Failed");
        result.Response.FailureReason.Should().Be("B2 cross-check call failed.");
        result.Response.FilenameMatches.Should().BeNull();
        result.Response.ContentMatches.Should().BeNull();
    }

    [Fact]
    public async Task Test_GetResultsAsync_FailedWithUnparsableResultDetail_ReturnsNullFailureReason()
    {
        // Arrange — defensive branch: malformed result_detail JSON never throws out of the service.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Failed",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow,
            ResultDetail = "{ not valid json"
        });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(100234, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        result.Response!.FailureReason.Should().BeNull();
    }

    // =================================================================================================
    // CAS-038 §13 "KeywordSearchServiceTests" — optional created_at range (V11–V13, SC-1/SC-2 payload
    // half, SC-7). The Worker half of SC-1/SC-2 lives in Worker.Tests.KeywordSearchTaskProcessorTests.
    // =================================================================================================

    private static Fixture FixtureCapturingRequestParameters(Action<string?> capture, List<string>? callOrder = null)
    {
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "KeywordSearch", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100234)
            .AndDoes(callInfo =>
            {
                capture(callInfo.ArgAt<string?>(4));
                callOrder?.Add("insert-task");
            });
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder?.Add("audit"));
        return fixture;
    }

    private static async Task AssertNoTaskAndNoAuditAsync(Fixture fixture)
    {
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 SC-1 (payload half): no date fields -> FromDate/ToDate persisted as null ------------

    [Fact]
    public async Task Test_SC1_KeywordOnlyRequestPersistsNullDatesAndSearchesUnfiltered()
    {
        // Arrange
        string? persisted = null;
        var fixture = FixtureCapturingRequestParameters(json => persisted = json);
        var sut = fixture.CreateSut();

        // Act — the pre-CAS-038 request shape: no FromDate/ToDate at all.
        var result = await sut.SubmitAsync(ValidRequest(), submittedByUserId: 7, CancellationToken.None);

        // Assert — accepted exactly as before; the persisted payload carries null dates.
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
        persisted.Should().NotBeNull();
        var payload = System.Text.Json.JsonSerializer.Deserialize<KeywordSearchRequestDto>(persisted!)!;
        payload.Term.Should().Be("confidential settlement");
        payload.Bucket.Should().Be(ConfiguredBucket);
        payload.Extension.Should().Be("All");
        payload.FromDate.Should().BeNull();
        payload.ToDate.Should().BeNull();
    }

    // ---- CAS-038 SC-2 (payload + audit half): both dates persisted, audit written before return -----

    [Fact]
    public async Task Test_SC2_DateRangeRequestPersistsAndIsPassedToCandidateQuery()
    {
        // Arrange — a fixed, past, valid range (From < To, neither in the future).
        var fromDate = new DateOnly(2025, 1, 10);
        var toDate = new DateOnly(2025, 3, 31);
        string? persisted = null;
        var callOrder = new List<string>();
        var fixture = FixtureCapturingRequestParameters(json => persisted = json, callOrder);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitAsync(
            ValidRequest() with { FromDate = fromDate, ToDate = toDate }, submittedByUserId: 7, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert — accepted; dates persisted verbatim into request_parameters (ISO yyyy-MM-dd).
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
        persisted.Should().NotBeNull();
        persisted.Should().Contain("\"2025-01-10\"").And.Contain("\"2025-03-31\"");
        var payload = System.Text.Json.JsonSerializer.Deserialize<KeywordSearchRequestDto>(persisted!)!;
        payload.FromDate.Should().Be(fromDate);
        payload.ToDate.Should().Be(toDate);
        payload.Term.Should().Be("confidential settlement");

        // Assert — KeywordSearchSubmitted audit row (shape unchanged, §11) written before the return.
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.KeywordSearchSubmitted &&
                e.TabModule == "Keyword Search" &&
                e.TaskQueueId == 100234 &&
                e.UserId == 7 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
        callOrder.Should().Equal("insert-task", "audit", "response-returned");
    }

    // ---- CAS-038 V11 / SC-7: exactly one date supplied -> 422, no task, no audit ---------------------

    [Theory]
    [InlineData("2025-01-10", null)]
    [InlineData(null, "2025-01-10")]
    public async Task Test_V11_OnlyOneDateSuppliedReturns422AndInsertsNoTask(string? fromDate, string? toDate)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest() with
        {
            FromDate = fromDate is null ? null : DateOnly.Parse(fromDate, System.Globalization.CultureInfo.InvariantCulture),
            ToDate = toDate is null ? null : DateOnly.Parse(toDate, System.Globalization.CultureInfo.InvariantCulture)
        };

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        result.ValidationError.Should().Be("'fromDate' and 'toDate' must be supplied together.");
        result.Response.Should().BeNull();
        await AssertNoTaskAndNoAuditAsync(fixture);
    }

    // ---- CAS-038 V12 / SC-7: From after To -> 422, no task, no audit ---------------------------------

    [Fact]
    public async Task Test_V12_FromDateAfterToDateReturns422AndInsertsNoTask()
    {
        // Arrange — From is one day after To; both in the past, so only V12 can fire.
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest() with { FromDate = new DateOnly(2025, 3, 2), ToDate = new DateOnly(2025, 3, 1) };

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        result.ValidationError.Should().Be("'fromDate' must be on or before 'toDate'.");
        await AssertNoTaskAndNoAuditAsync(fixture);
    }

    // ---- CAS-038 V13 / SC-7: either date after today (UTC) -> 422, no task, no audit -----------------

    [Fact]
    public async Task Test_V13_FutureDateReturns422AndInsertsNoTask()
    {
        // Arrange — today+2 is used (not today+1) so a UTC-midnight rollover between computing
        // "today" here and inside the service can never turn a future date into a valid one.
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        var futureCases = new[]
        {
            (From: todayUtc.AddDays(-1), To: todayUtc.AddDays(2)), // only To in the future
            (From: todayUtc.AddDays(2), To: todayUtc.AddDays(3))   // both in the future (From <= To, so V12 cannot fire)
        };

        foreach (var (from, to) in futureCases)
        {
            var fixture = new Fixture();
            var sut = fixture.CreateSut();

            // Act
            var result = await sut.SubmitAsync(ValidRequest() with { FromDate = from, ToDate = to }, submittedByUserId: 1, CancellationToken.None);

            // Assert
            result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed, $"range {from:yyyy-MM-dd}..{to:yyyy-MM-dd} includes a future date");
            result.ValidationError.Should().Be("Dates cannot be in the future.");
            await AssertNoTaskAndNoAuditAsync(fixture);
        }

        // Boundary — today (UTC) itself is not "in the future" and is accepted. If the service's own
        // clock has rolled past midnight since todayUtc was computed, todayUtc is yesterday: still valid.
        string? persisted = null;
        var boundaryFixture = FixtureCapturingRequestParameters(json => persisted = json);
        var boundaryResult = await boundaryFixture.CreateSut().SubmitAsync(
            ValidRequest() with { FromDate = todayUtc, ToDate = todayUtc }, submittedByUserId: 1, CancellationToken.None);
        boundaryResult.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
        persisted.Should().NotBeNull();
    }

    // =================================================================================================
    // CAS-038 Amendment A2 — date-only search (V1 amended, V14, SC-8).
    // =================================================================================================

    private const string V1Message = "'term' is required and must be at most 200 characters.";

    // ---- CAS-038 A2 V1 (amended): empty term allowed only when both dates are supplied ---------------

    [Fact]
    public async Task Test_A2_V1_EmptyTermAcceptedOnlyWithBothDates()
    {
        var fromDate = new DateOnly(2025, 1, 1);
        var toDate = new DateOnly(2025, 1, 31);

        // Empty / whitespace term + both dates (31-day range) -> accepted.
        foreach (var blankTerm in new[] { "", "   " })
        {
            var acceptedFixture = FixtureCapturingRequestParameters(_ => { });
            var accepted = await acceptedFixture.CreateSut().SubmitAsync(
                ValidRequest(blankTerm) with { FromDate = fromDate, ToDate = toDate }, submittedByUserId: 1, CancellationToken.None);

            accepted.Outcome.Should().Be(SearchSubmitOutcome.Accepted, $"term '{blankTerm}' with both dates is a date-only search");
        }

        // Empty term + no dates, or + only one date -> V1 message (V1 fires before V11).
        var rejectedCases = new (DateOnly? From, DateOnly? To)[]
        {
            (null, null),
            (fromDate, null),
            (null, toDate)
        };

        foreach (var (from, to) in rejectedCases)
        {
            var fixture = new Fixture();
            var result = await fixture.CreateSut().SubmitAsync(
                ValidRequest("") with { FromDate = from, ToDate = to }, submittedByUserId: 1, CancellationToken.None);

            result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
            result.ValidationError.Should().Be(V1Message, $"empty term with from={from}, to={to} is not a date-only search");
            await AssertNoTaskAndNoAuditAsync(fixture);
        }

        // A non-empty term is still capped at 200 chars, even with both dates.
        var tooLongFixture = new Fixture();
        var tooLong = await tooLongFixture.CreateSut().SubmitAsync(
            ValidRequest(new string('a', 201)) with { FromDate = fromDate, ToDate = toDate }, submittedByUserId: 1, CancellationToken.None);

        tooLong.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        tooLong.ValidationError.Should().Be(V1Message);
        await AssertNoTaskAndNoAuditAsync(tooLongFixture);
    }

    // ---- CAS-038 A2 V14: date-only range capped at 31 inclusive days; no cap with a term -------------

    [Fact]
    public async Task Test_V14_DateOnlyRangeOver31DaysReturns422AndInsertsNoTask()
    {
        // 01/01/2025–02/01/2025 is 32 inclusive days -> rejected, no task, no audit.
        var overFixture = new Fixture();
        var over = await overFixture.CreateSut().SubmitAsync(
            ValidRequest("") with { FromDate = new DateOnly(2025, 1, 1), ToDate = new DateOnly(2025, 2, 1) },
            submittedByUserId: 1, CancellationToken.None);

        over.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        over.ValidationError.Should().Be("Without a keyword, the date range can be at most 31 days.");
        over.Response.Should().BeNull();
        await AssertNoTaskAndNoAuditAsync(overFixture);

        // 01/01/2025–01/31/2025 is exactly 31 inclusive days -> accepted.
        var boundaryFixture = FixtureCapturingRequestParameters(_ => { });
        var boundary = await boundaryFixture.CreateSut().SubmitAsync(
            ValidRequest("") with { FromDate = new DateOnly(2025, 1, 1), ToDate = new DateOnly(2025, 1, 31) },
            submittedByUserId: 1, CancellationToken.None);

        boundary.Outcome.Should().Be(SearchSubmitOutcome.Accepted);

        // Keyword + date searches have no range limit.
        var keywordFixture = FixtureCapturingRequestParameters(_ => { });
        var keyword = await keywordFixture.CreateSut().SubmitAsync(
            ValidRequest("x") with { FromDate = new DateOnly(2025, 1, 1), ToDate = new DateOnly(2025, 6, 30) },
            submittedByUserId: 1, CancellationToken.None);

        keyword.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
    }

    // ---- CAS-038 A2 SC-8: date-only request accepted, Term persisted as "", audit before return -------

    [Fact]
    public async Task Test_A2_SC8_DateOnlyRequestAcceptedAndPersistsEmptyTerm()
    {
        // Arrange
        var fromDate = new DateOnly(2025, 1, 1);
        var toDate = new DateOnly(2025, 1, 15);
        string? persisted = null;
        var callOrder = new List<string>();
        var fixture = FixtureCapturingRequestParameters(json => persisted = json, callOrder);
        var sut = fixture.CreateSut();

        // Act — whitespace term proves the trimmed-empty value is what gets persisted.
        var result = await sut.SubmitAsync(
            ValidRequest("   ") with { FromDate = fromDate, ToDate = toDate }, submittedByUserId: 7, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert — the 202-equivalent Accepted outcome.
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
        result.Response.Should().NotBeNull();
        result.Response!.TaskId.Should().Be(100234);
        result.Response.Type.Should().Be("KeywordSearch");
        result.Response.Status.Should().Be("Queued");

        // Assert — payload Term == "" plus both dates.
        persisted.Should().NotBeNull();
        var payload = System.Text.Json.JsonSerializer.Deserialize<KeywordSearchRequestDto>(persisted!)!;
        payload.Term.Should().Be(string.Empty);
        payload.FromDate.Should().Be(fromDate);
        payload.ToDate.Should().Be(toDate);
        payload.Bucket.Should().Be(ConfiguredBucket);
        payload.Extension.Should().Be("All");

        // Assert — KeywordSearchSubmitted audited before the return.
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.KeywordSearchSubmitted &&
                e.TabModule == "Keyword Search" &&
                e.TaskQueueId == 100234 &&
                e.UserId == 7 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
        callOrder.Should().Equal("insert-task", "audit", "response-returned");
    }

    // =================================================================================================
    // CAS-047 — search modes (§9 V1-V3, §10 SC-1/SC-9/SC-6)
    // =================================================================================================

    private static readonly DateOnly Cas047From = new(2025, 1, 10);
    private static readonly DateOnly Cas047To = new(2025, 1, 12);

    [Fact]
    public async Task Test_SC1_SubmitFilenamePersistsModeAndAuditsBeforeAccept()
    {
        // Arrange
        const string term = "secret-term-4471";
        var fixture = new Fixture();
        var callOrder = new List<string>();
        string? persisted = null;
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "KeywordSearch", 7, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100234)
            .AndDoes(ci => { persisted = ci.ArgAt<string?>(4); callOrder.Add("insert-task"); });
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();
        var request = new SearchRequest { Term = term, Bucket = ConfiguredBucket, Extension = "All", Mode = "Filename" };

        // Act
        var result = await sut.SubmitAsync(request, submittedByUserId: 7, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
        System.Text.Json.JsonSerializer.Deserialize<KeywordSearchRequestDto>(persisted!)!.Mode.Should().Be("Filename");
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.KeywordSearchSubmitted &&
                e.ResultDetail != null && e.ResultDetail.Contains("Filename") && !e.ResultDetail.Contains(term)),
            Arg.Any<CancellationToken>());
        callOrder.Should().Equal("insert-task", "audit", "response-returned");
    }

    [Fact]
    public async Task Test_SC6_LegacyTaskUnchanged()
    {
        // Arrange — absent mode: accepted as today, Mode persisted as null, audit detail carries no mode.
        var fixture = new Fixture();
        string? persisted = null;
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "KeywordSearch", 1, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(5)
            .AndDoes(ci => persisted = ci.ArgAt<string?>(4));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitAsync(ValidRequest(), submittedByUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
        System.Text.Json.JsonSerializer.Deserialize<KeywordSearchRequestDto>(persisted!)!.Mode.Should().BeNull();
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.ResultDetail == null), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Filename")]
    [InlineData("Keyword")]
    [InlineData("Date")]
    public async Task Test_SC9_TermNeverLogged(string mode)
    {
        // Arrange
        const string term = "secret-term-4471";
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(5);
        var sut = fixture.CreateSut();
        var request = mode == "Date"
            ? new SearchRequest { Term = "", Bucket = ConfiguredBucket, Extension = "All", Mode = mode, FromDate = Cas047From, ToDate = Cas047To }
            : new SearchRequest { Term = term, Bucket = ConfiguredBucket, Extension = "All", Mode = mode };

        // Act
        await sut.SubmitAsync(request, submittedByUserId: 1, CancellationToken.None);

        // Assert — no log line and no audit entry field carries the term.
        foreach (var call in fixture.Logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length >= 5 && args[2] is not null && args[4] is Delegate formatter)
            {
                ((string?)formatter.DynamicInvoke(args[2], args[3] as Exception)).Should().NotContain(term);
            }
        }

        foreach (var call in fixture.AuditLogService.ReceivedCalls())
        {
            call.GetArguments().Select(a => a is AuditLogEntry e ? e.ResultDetail ?? string.Empty : string.Empty)
                .Should().OnlyContain(d => !d.Contains(term));
        }
    }

    [Theory]
    [InlineData("filename")]
    [InlineData("KEYWORD")]
    [InlineData("Other")]
    [InlineData("")]
    public async Task Test_V1_UnknownModeRejected(string mode)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest() with { Mode = mode };

        // Act
        var result = await sut.SubmitAsync(request, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        result.ValidationError.Should().Contain("mode");
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Filename", "ab", false)]
    [InlineData("Filename", "  ab  ", false)]
    [InlineData("Filename", "abc", true)]
    [InlineData("Keyword", "ab", false)]
    [InlineData("Keyword", "abc", true)]
    public async Task Test_V2_FilenameAndKeywordMinThreeCharsAndForbidDates(string mode, string term, bool accepted)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(5);
        var sut = fixture.CreateSut();
        var request = ValidRequest(term) with { Mode = mode };

        // Act
        var result = await sut.SubmitAsync(request, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(accepted ? SearchSubmitOutcome.Accepted : SearchSubmitOutcome.ValidationFailed);
        if (!accepted)
        {
            result.ValidationError.Should().Contain("3");
        }
    }

    [Theory]
    [InlineData("Filename")]
    [InlineData("Keyword")]
    public async Task Test_V2_FilenameAndKeywordMinThreeCharsAndForbidDates_DatesRejected(string mode)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest("abcd") with { Mode = mode, FromDate = Cas047From, ToDate = Cas047To };

        // Act
        var result = await sut.SubmitAsync(request, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
        result.ValidationError.Should().Contain("not allowed");
    }

    [Fact]
    public async Task Test_V2_FilenameAndKeywordMinThreeCharsAndForbidDates_TermOverMaxRejected()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest(new string('a', 201)) with { Mode = "Filename" };

        // Act
        var result = await sut.SubmitAsync(request, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
    }

    [Theory]
    [InlineData("abc", true, true)]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    public async Task Test_V3_DateModeRequiresBlankTermAndRange_Rejected(string term, bool hasFrom, bool hasTo)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidRequest(term) with { Mode = "Date", FromDate = hasFrom ? Cas047From : null, ToDate = hasTo ? Cas047To : null };

        // Act
        var result = await sut.SubmitAsync(request, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.ValidationFailed);
    }

    [Fact]
    public async Task Test_V3_DateModeRequiresBlankTermAndRange()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(5);
        var sut = fixture.CreateSut();
        var request = ValidRequest("") with { Mode = "Date", FromDate = Cas047From, ToDate = Cas047To };

        // Act
        var result = await sut.SubmitAsync(request, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SearchSubmitOutcome.Accepted);
    }
}
