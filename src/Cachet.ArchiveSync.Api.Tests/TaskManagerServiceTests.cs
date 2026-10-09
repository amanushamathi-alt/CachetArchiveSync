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
/// Unit tests for <see cref="TaskManagerService"/> (CAS-016 §5/§8/§11, FR-23) — the spec's §13 Test
/// Plan "TaskManagerServiceTests" assignment: V1, V5, plus V3 (audit-before-response ordering). The
/// real-database proof of the underlying <c>dbo.task_queue_restart_ins</c> outcomes (SC-1, SC-3,
/// SC-5, V4) lives in <c>Cachet.ArchiveSync.Api.IntegrationTests.TaskQueueRestartRepositoryTests</c>;
/// this class mocks <see cref="ITaskQueueRepository"/> and only proves the service layer's own
/// mapping/audit-ordering behavior.
/// </summary>
public sealed class TaskManagerServiceTests
{
    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<TaskManagerService> Logger { get; } = Substitute.For<ILogger<TaskManagerService>>();

        public TaskManagerService CreateSut(string? resumeMaxAgeHours = null) =>
            new(TaskQueueRepository, AuditLogService, Logger,
                resumeMaxAgeHours is null
                    ? null
                    : new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:ResumeMaxAgeHours"] = resumeMaxAgeHours })
                        .Build());
    }

    // ---- CAS-054 SC-8: restart audit detail names the resume and the carry-over, before responding ----

    [Fact]
    public async Task Test_SC8_RestartAuditDetailNamesResumeAndCarryOver()
    {
        // Arrange
        var fixture = new Fixture();
        var callOrder = new List<string>();
        fixture.TaskQueueRepository
            .RestartAsync(100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult
            {
                Outcome = RestartTaskOutcome.Restarted,
                NewTaskQueueId = 100999,
                ResumedFromFileIndexId = 31149,
                CarriedOverCount = 17,
                ResumeNote = "Resumed"
            });
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();

        // Act
        await sut.RestartAsync(100234, requestingUserId: 9, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert - exact detail text (integers and task ids only), written before the response.
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.TaskRestart &&
                e.TaskQueueId == 100999 &&
                e.ResultDetail == "Resumed from file_index_id 31149; 17 match result(s) carried over from task 100234."),
            Arg.Any<CancellationToken>());
        callOrder.Should().Equal("audit", "response-returned");
    }

    [Fact]
    public async Task Test_SC8_RestartPassesConfiguredResumeMaxAgeToRepository()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .RestartAsync(100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.NotRestartable });

        // Act
        await fixture.CreateSut("48").RestartAsync(100234, requestingUserId: 9, CancellationToken.None);
        await fixture.CreateSut().RestartAsync(100234, requestingUserId: 9, CancellationToken.None);

        // Assert - configured value, then the 24 h default.
        await fixture.TaskQueueRepository.Received(1).RestartAsync(
            100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), 48);
        await fixture.TaskQueueRepository.Received(1).RestartAsync(
            100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), 24);
    }

    // ---- CAS-054 SC-11 (service half): full-restart audit detail states the reason --------------------

    [Theory]
    [InlineData("CheckpointTooOld", "Full restart (checkpoint older than 24 h)")]
    [InlineData("NoCheckpoint", "Full restart (no checkpoint)")]
    [InlineData(null, "Full restart (no checkpoint)")]
    public async Task Test_SC11_RestartBeyondMaxAgeIsFullRestart_AuditDetail(string? resumeNote, string expectedDetail)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .RestartAsync(100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult
            {
                Outcome = RestartTaskOutcome.Restarted,
                NewTaskQueueId = 100999,
                ResumeNote = resumeNote
            });
        var sut = fixture.CreateSut();

        // Act
        await sut.RestartAsync(100234, requestingUserId: 9, CancellationToken.None);

        // Assert
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.ResultDetail == expectedDetail), Arg.Any<CancellationToken>());
    }

    // ---- CAS-054 V7: Worker:ResumeMaxAgeHours must be 1-720 --------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("721")]
    [InlineData("-5")]
    public void Test_V7_ResumeMaxAgeHours_OutOfRange_IsRejected(string value)
    {
        // Arrange
        var fixture = new Fixture();

        // Act
        var act = () => fixture.CreateSut(value);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Worker:ResumeMaxAgeHours*1 and 720*");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("720")]
    public void Test_V7_ResumeMaxAgeHours_Boundaries_AreAccepted(string value)
    {
        // Arrange
        var fixture = new Fixture();

        // Act
        var act = () => fixture.CreateSut(value);

        // Assert
        act.Should().NotThrow();
    }

    // ---- V1: only a Failed row may be restarted; NotRestartable is passed through, never audited ----

    [Fact]
    public async Task Test_V1_RestartRejectedWhenNotFailed()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .RestartAsync(100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.NotRestartable });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RestartAsync(100234, requestingUserId: 9, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.NotRestartable);
        result.NewTaskQueueId.Should().BeNull();
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- V5: only the original submitter may restart; NotOwner is passed through, never audited -----

    [Fact]
    public async Task Test_V5_RestartRejectedWhenNotOwner()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .RestartAsync(100234, 7, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.NotOwner });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RestartAsync(100234, requestingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.NotOwner);
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: unknown taskId is passed through unaudited, same as NotOwner/NotRestartable

    [Fact]
    public async Task Test_RestartNotFound_PassedThroughWithoutAuditWrite()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .RestartAsync(999999, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.NotFound });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RestartAsync(999999, requestingUserId: 9, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.NotFound);
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- V3: the audit row is written before RestartAsync returns on a successful restart -----------

    [Fact]
    public async Task Test_V3_RestartWritesAuditLogBeforeResponding()
    {
        // Arrange
        var fixture = new Fixture();
        var callOrder = new List<string>();
        fixture.TaskQueueRepository
            .RestartAsync(100234, 9, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(new RestartTaskResult { Outcome = RestartTaskOutcome.Restarted, NewTaskQueueId = 100999 })
            .AndDoes(_ => callOrder.Add("restart"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RestartAsync(100234, requestingUserId: 9, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert — audit happens strictly before the result is handed back to the controller, and
        // the audit row's task_queue_id references the NEW row (100999), never the original (100234).
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        result.NewTaskQueueId.Should().Be(100999);

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.TaskRestart &&
                e.TabModule == "Task Manager" &&
                e.TaskQueueId == 100999 &&
                e.UserId == 9 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());

        callOrder.Should().Equal("restart", "audit", "response-returned");
    }

    // =================================================================================================
    // CAS-030 §13 "TaskManagerServiceTests" — own-scoped read surface and acknowledge (§6, §12).
    // =================================================================================================

    private static TaskQueueRow Row(
        int taskId = 100234,
        string taskType = "KeywordSearch",
        string status = "Completed",
        int submittedByUserId = 9,
        string? requestParameters = null,
        string? resultDetail = null,
        DateTimeOffset? resultAcknowledgedAt = null) => new()
        {
            TaskQueueId = taskId,
            TaskType = taskType,
            Status = status,
            SubmittedByUserId = submittedByUserId,
            SubmittedAt = new DateTimeOffset(2026, 9, 24, 14, 5, 1, TimeSpan.Zero),
            StartedAt = new DateTimeOffset(2026, 9, 24, 14, 5, 3, TimeSpan.Zero),
            CompletedAt = status is "Completed" or "Failed" ? new DateTimeOffset(2026, 9, 24, 15, 12, 40, TimeSpan.Zero) : null,
            RequestParameters = requestParameters,
            ResultDetail = resultDetail,
            ResultAcknowledgedAt = resultAcknowledgedAt
        };

    // ---- V3 (service level): a task owned by another user is indistinguishable from a missing one ----

    [Fact]
    public async Task Test_V3_GetTask_NotOwned_ReturnsNotFound()
    {
        // Arrange — task 100234 exists but belongs to user 7; task 999999 does not exist at all.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(Row(submittedByUserId: 7));
        fixture.TaskQueueRepository.GetAsync(999999, Arg.Any<CancellationToken>())
            .Returns((TaskQueueRow?)null);
        var sut = fixture.CreateSut();

        // Act
        var notOwned = await sut.GetAsync(100234, requestingUserId: 9, "m.alvarez", CancellationToken.None);
        var missing = await sut.GetAsync(999999, requestingUserId: 9, "m.alvarez", CancellationToken.None);

        // Assert — both return null (the controller maps null to the one task-not-found body).
        notOwned.Should().BeNull("ownership is enforced against task_queue_get's submitted_by_user_id (§12)");
        missing.Should().BeNull();
    }

    // ---- SC-4: fixed term/extension projection for KeywordSearch only; raw JSON never passed through ----

    [Theory]
    [InlineData("KeywordSearch", "valid", true)]
    [InlineData("ContentScan", "valid", false)]
    [InlineData("SyncCopy", "valid", false)]
    [InlineData("KeywordSearch", "malformed", false)]
    [InlineData("KeywordSearch", "missing", false)]
    [InlineData("KeywordSearch", "json-null", false)]
    public async Task Test_SC4_GetTask_ProjectsSearchParametersOnly(string taskType, string payloadKind, bool expectProjection)
    {
        // Arrange
        const string SecretBucket = "cachet-archive-prod-bucket-marker-cas030";
        var rawRequestParameters = payloadKind switch
        {
            "valid" => System.Text.Json.JsonSerializer.Serialize(new KeywordSearchRequestDto
            {
                Term = "invoice",
                Bucket = SecretBucket,
                Extension = "PDF"
            }),
            "malformed" => "{not-json",
            "json-null" => "null",
            _ => null
        };
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(Row(taskType: taskType, requestParameters: rawRequestParameters));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(100234, requestingUserId: 9, "m.alvarez", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.TaskId.Should().Be(100234);
        result.Type.Should().Be(taskType);
        result.SubmittedBy.Should().Be("m.alvarez");
        result.IsAcknowledged.Should().BeFalse();
        if (expectProjection)
        {
            result.SearchParameters.Should().BeEquivalentTo(new TaskSearchParameters { Term = "invoice", Extension = "PDF" });
        }
        else
        {
            result.SearchParameters.Should().BeNull();
        }

        // The serialized response never carries the raw request_parameters string or its extra fields.
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        if (payloadKind is "valid" or "malformed")
        {
            serialized.Should().NotContain(rawRequestParameters!);
        }

        serialized.Should().NotContain(SecretBucket, "bucket is not part of the fixed projection (§12)");
        serialized.Should().NotContain("RequestParameters");
    }

    // ---- V4: the procedure's "not finished" outcome is passed through as NotTerminal (-> 409) -----------

    [Fact]
    public async Task Test_V4_Acknowledge_MapsNotTerminalOutcome()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .AcknowledgeAsync(100234, 9, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AcknowledgeTaskOutcome.NotTerminal);
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.AcknowledgeAsync(100234, requestingUserId: 9, CancellationToken.None);

        // Assert — passed through; acknowledge is never audited (§12, §15 Q2), so no audit expectation.
        outcome.Should().Be(AcknowledgeTaskOutcome.NotTerminal);
    }

    // ---- Coverage-gap closure (not a spec-mapped SC/V ID; same precedent as the restart NotFound test
    //      above): list mapping, summary pass-through, Failed resultDetail, and the other outcomes.

    [Fact]
    public async Task Test_ListTasks_MapsRowsToItemsAndPassesPagingThrough()
    {
        // Arrange
        var fixture = new Fixture();
        IReadOnlyList<TaskQueueListRow> rows = new[]
        {
            new TaskQueueListRow
            {
                TaskQueueId = 2, TaskType = "KeywordSearch", SubmittedByUserId = 9, Username = "m.alvarez",
                SubmittedAt = DateTimeOffset.UtcNow, Status = "Queued"
            },
            new TaskQueueListRow
            {
                TaskQueueId = 1, TaskType = "ContentScan", SubmittedByUserId = 9, Username = "m.alvarez",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1), Status = "Completed",
                ResultAcknowledgedAt = DateTimeOffset.UtcNow
            }
        };
        fixture.TaskQueueRepository.ListAsync(9, "KeywordSearch", null, 2, 25, Arg.Any<CancellationToken>())
            .Returns((rows, 27));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(9, "KeywordSearch", null, 2, 25, CancellationToken.None);

        // Assert
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(25);
        result.TotalCount.Should().Be(27);
        result.Items.Select(i => i.TaskId).Should().Equal(2, 1);
        result.Items[0].StartTime.Should().BeNull("startTime is null while Queued (§6)");
        result.Items[0].IsAcknowledged.Should().BeFalse();
        result.Items[1].IsAcknowledged.Should().BeTrue();
        result.Items[1].SubmittedBy.Should().Be("m.alvarez");
        result.Items[1].CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Test_Summary_PassesRepositoryCountsThrough()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetSummaryAsync(9, Arg.Any<CancellationToken>())
            .Returns(new TaskSummaryResponse { ActiveCount = 1, UnacknowledgedTerminalCount = 2 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetSummaryAsync(9, CancellationToken.None);

        // Assert
        result.Should().Be(new TaskSummaryResponse { ActiveCount = 1, UnacknowledgedTerminalCount = 2 });
    }

    [Theory]
    [InlineData("Failed", "B2 API timeout after 3 retries")]
    [InlineData("Completed", null)]
    public async Task Test_GetTask_ResultDetailOnlyForFailed(string status, string? expectedDetail)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(Row(taskType: "ContentScan", status: status, resultDetail: "B2 API timeout after 3 retries",
                resultAcknowledgedAt: DateTimeOffset.UtcNow));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(100234, requestingUserId: 9, "m.alvarez", CancellationToken.None);

        // Assert
        result!.ResultDetail.Should().Be(expectedDetail);
        result.IsAcknowledged.Should().BeTrue();
    }

    [Theory]
    [InlineData(AcknowledgeTaskOutcome.Acknowledged)]
    [InlineData(AcknowledgeTaskOutcome.NotFound)]
    public async Task Test_Acknowledge_PassesOutcomeThrough(AcknowledgeTaskOutcome repositoryOutcome)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .AcknowledgeAsync(100234, 9, "api-svc", Arg.Any<CancellationToken>())
            .Returns(repositoryOutcome);
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.AcknowledgeAsync(100234, requestingUserId: 9, CancellationToken.None);

        // Assert
        outcome.Should().Be(repositoryOutcome);
    }

    // ---- CAS-038 SC-6: projection carries the date range; null for keyword-only and legacy payloads ----

    [Fact]
    public async Task Test_SC6_TaskProjectionIncludesDatesAndToleratesLegacyPayload()
    {
        // Arrange — three KeywordSearch rows owned by user 9:
        //   100 — a CAS-038 date-range payload,
        //   101 — a CAS-038 keyword-only payload (date keys present, null),
        //   102 — a pre-CAS-038 payload with no date keys at all (hand-written legacy JSON).
        var fromDate = new DateOnly(2025, 1, 10);
        var toDate = new DateOnly(2025, 3, 31);
        var dateRangePayload = System.Text.Json.JsonSerializer.Serialize(new KeywordSearchRequestDto
        {
            Term = "invoice",
            Bucket = "cachet-archive-prod",
            Extension = "PDF",
            FromDate = fromDate,
            ToDate = toDate
        });
        var keywordOnlyPayload = System.Text.Json.JsonSerializer.Serialize(new KeywordSearchRequestDto
        {
            Term = "invoice",
            Bucket = "cachet-archive-prod",
            Extension = "PDF"
        });
        const string legacyPayload = "{\"Term\":\"invoice\",\"Bucket\":\"cachet-archive-prod\",\"Extension\":\"PDF\"}";

        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100, Arg.Any<CancellationToken>()).Returns(Row(taskId: 100, requestParameters: dateRangePayload));
        fixture.TaskQueueRepository.GetAsync(101, Arg.Any<CancellationToken>()).Returns(Row(taskId: 101, requestParameters: keywordOnlyPayload));
        fixture.TaskQueueRepository.GetAsync(102, Arg.Any<CancellationToken>()).Returns(Row(taskId: 102, requestParameters: legacyPayload));
        var sut = fixture.CreateSut();

        // Act
        var dateRange = await sut.GetAsync(100, requestingUserId: 9, "m.alvarez", CancellationToken.None);
        var keywordOnly = await sut.GetAsync(101, requestingUserId: 9, "m.alvarez", CancellationToken.None);
        var legacyAct = async () => await sut.GetAsync(102, requestingUserId: 9, "m.alvarez", CancellationToken.None);

        // Assert — date-range task projects both dates alongside term/extension.
        dateRange!.SearchParameters.Should().BeEquivalentTo(new TaskSearchParameters
        {
            Term = "invoice",
            Extension = "PDF",
            FromDate = fromDate,
            ToDate = toDate
        });

        // Assert — keyword-only task: dates null.
        keywordOnly!.SearchParameters.Should().BeEquivalentTo(new TaskSearchParameters { Term = "invoice", Extension = "PDF" });
        keywordOnly.SearchParameters!.FromDate.Should().BeNull();
        keywordOnly.SearchParameters.ToDate.Should().BeNull();

        // Assert — legacy payload without date keys: no exception, dates null, term/extension intact.
        var legacy = (await legacyAct.Should().NotThrowAsync()).Subject;
        legacy!.SearchParameters.Should().NotBeNull();
        legacy.SearchParameters!.Term.Should().Be("invoice");
        legacy.SearchParameters.Extension.Should().Be("PDF");
        legacy.SearchParameters.FromDate.Should().BeNull();
        legacy.SearchParameters.ToDate.Should().BeNull();
    }

    // ---- CAS-047 SC-7: GET /tasks/{id} searchParameters.mode (list half unproven — see report) ----

    [Theory]
    [InlineData("Filename")]
    [InlineData("Keyword")]
    [InlineData("Date")]
    [InlineData(null)]
    public async Task Test_SC7_TaskReadsExposeMode(string? mode)
    {
        // Arrange — legacy payload (no mode) must read back as null.
        var payload = System.Text.Json.JsonSerializer.Serialize(new KeywordSearchRequestDto
        {
            Term = "invoice",
            Bucket = "b",
            Extension = "PDF",
            Mode = mode
        });
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(Row(requestParameters: payload));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(100234, requestingUserId: 9, "m.alvarez", CancellationToken.None);

        // Assert
        result!.SearchParameters!.Mode.Should().Be(mode);
    }

    [Fact]
    public async Task Test_SC7_TaskReadsExposeMode_NonSearchTaskHasNoSearchParameters()
    {
        // Arrange
        var payload = System.Text.Json.JsonSerializer.Serialize(new KeywordSearchRequestDto
        {
            Term = "x",
            Bucket = "b",
            Extension = "All",
            Mode = "Filename"
        });
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(Row(taskType: "ContentScan", requestParameters: payload));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(100234, requestingUserId: 9, "m.alvarez", CancellationToken.None);

        // Assert
        result!.SearchParameters.Should().BeNull();
    }
}
