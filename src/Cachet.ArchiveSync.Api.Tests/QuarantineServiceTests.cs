using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="QuarantineService"/> (CAS-006 §6.3, FR-09) — the spec's §13 Test
/// Plan "QuarantineAddServiceTests". Proves V6–V8 and, at the service layer, SC-9/SC-10's
/// audit-before-response ordering and no-duplicate-insert-attempt behavior (the real-database
/// proof of SC-9/SC-10's filtered-unique-index effect lives in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.QuarantineEntryRepositoryTests</c>).
/// </summary>
public sealed class QuarantineServiceTests
{
    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();
        public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<QuarantineService> Logger { get; } = Substitute.For<ILogger<QuarantineService>>();

        public QuarantineService CreateSut() =>
            new(TaskQueueRepository, SearchResultRepository, QuarantineEntryRepository, AuditLogService, Logger);
    }

    private static TaskQueueRow ValidTask(int taskId = 100234) => new()
    {
        TaskQueueId = taskId,
        TaskType = "KeywordSearch",
        Status = "Completed",
        SubmittedByUserId = 1,
        SubmittedAt = DateTimeOffset.UtcNow
    };

    private static SearchResultRow MatchRow(int searchResultId, int taskQueueId = 100234) => new()
    {
        SearchResultId = searchResultId,
        TaskQueueId = taskQueueId,
        FileIndexId = searchResultId + 1000,
        MatchType = "Filename",
        MatchStatus = "Flagged",
        CreatedAt = DateTimeOffset.UtcNow,
        FileName = "agreement-004.pdf",
        RelativePath = "contracts/2019/agreement-004.pdf",
        FileSizeBytes = 482113,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    // ---- V6: fileIds must be non-empty ------------------------------------------------------------

    [Fact]
    public async Task Test_V6_QuarantineEmptyFileIdsRejected()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = Array.Empty<int>() };

        // Act
        var result = await sut.AddAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineAddOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: unknown taskId -----------------------------------------------------------

    [Fact]
    public async Task Test_QuarantineTaskNotFound_ReturnsTaskNotFound()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(999, Arg.Any<CancellationToken>()).Returns((TaskQueueRow?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 999, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineAddOutcome.TaskNotFound);
    }

    // ---- V7: every fileId must resolve to a search_result row scoped to taskId --------------------

    [Fact]
    public async Task Test_V7_QuarantineFileIdNotInTaskReturns404()
    {
        // Arrange — search_result_id exists but belongs to a different task.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(ValidTask());
        fixture.SearchResultRepository.GetAsync(100501, Arg.Any<CancellationToken>())
            .Returns(MatchRow(100501, taskQueueId: 999999));
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineAddOutcome.FileNotFound);
        await fixture.QuarantineEntryRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V7_QuarantineUnknownSearchResultId_Returns404()
    {
        // Arrange — search_result_id does not resolve at all.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(ValidTask());
        fixture.SearchResultRepository.GetAsync(999999, Arg.Any<CancellationToken>()).Returns((SearchResultRow?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 999999 } };

        // Act
        var result = await sut.AddAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineAddOutcome.FileNotFound);
    }

    // ---- V8: an already-active file is rejected for that file only, not the whole batch -----------

    [Fact]
    public async Task Test_V8_QuarantineAlreadyActiveFileRejected()
    {
        // Arrange — two files requested: one succeeds, one is already actively quarantined.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(ValidTask());
        fixture.SearchResultRepository.GetAsync(100501, Arg.Any<CancellationToken>()).Returns(MatchRow(100501));
        fixture.SearchResultRepository.GetAsync(100502, Arg.Any<CancellationToken>()).Returns(MatchRow(100502));
        fixture.QuarantineEntryRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), 100501, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.Added, QuarantineEntryId = 100801 });
        fixture.QuarantineEntryRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), 100502, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.AlreadyActive });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501, 100502 } };

        // Act
        var result = await sut.AddAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — 200-shaped outcome (not the whole-batch 409), one item added, one warning.
        result.Outcome.Should().Be(QuarantineAddOutcome.Added);
        result.Response!.Items.Should().ContainSingle(i => i.FileId == 100801);
        result.Response.Warnings.Should().ContainSingle(w => w.Contains("100502"));
    }

    // ---- SC-9: valid add inserts, audits before the response, returns the added row(s) ------------

    [Fact]
    public async Task Test_SC9_QuarantineAddInsertsAndAuditsBeforeResponse()
    {
        // Arrange
        var fixture = new Fixture();
        var callOrder = new List<string>();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(ValidTask());
        fixture.SearchResultRepository.GetAsync(100501, Arg.Any<CancellationToken>()).Returns(MatchRow(100501));
        fixture.QuarantineEntryRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), 100501, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.Added, QuarantineEntryId = 100801 })
            .AndDoes(_ => callOrder.Add("insert"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddAsync(request, actingUserId: 9, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert
        result.Outcome.Should().Be(QuarantineAddOutcome.Added);
        result.Response!.Items.Should().ContainSingle(i =>
            i.FileId == 100801 &&
            i.Name == "agreement-004.pdf" &&
            i.Status == "Flagged");

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineAdd &&
                e.TabModule == "Keyword Search" &&
                e.TaskQueueId == 100234 &&
                e.UserId == 9 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());

        callOrder.Should().Equal("insert", "audit", "response-returned");
    }

    // ---- SC-10: a file already actively quarantined is rejected, never double-inserted ------------

    [Fact]
    public async Task Test_SC10_DuplicateQuarantineRejectedNotDoubleInserted()
    {
        // Arrange — every requested file is already actively quarantined.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(ValidTask());
        fixture.SearchResultRepository.GetAsync(100501, Arg.Any<CancellationToken>()).Returns(MatchRow(100501));
        fixture.QuarantineEntryRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), 100501, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.AlreadyActive });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — whole-request 409 (all files failed), insert attempted exactly once (the
        // stored-procedure/filtered-unique-index layer is the one that actually prevents the
        // duplicate row, proven separately in QuarantineEntryRepositoryTests), and no audit write.
        result.Outcome.Should().Be(QuarantineAddOutcome.AllAlreadyQuarantined);
        await fixture.QuarantineEntryRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), 100501, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }
}
