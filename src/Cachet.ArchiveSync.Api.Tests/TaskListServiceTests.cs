using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="TaskListService"/> (CAS-026 §6.1 <c>GET /tasks</c>, FR-19) — the spec's
/// §13 Test Plan "TaskListServiceTests" assignment: V1, V2, V3, SC-1, SC-2.
///
/// Scope note (mirrors <c>AuditLogListServiceTests</c>'s own precedent): the "does the ordering/
/// pagination/status filter actually reach SQL Server and return the right rows" aspect is a
/// repository-level concern proven separately by the Skip-marked, real-SQL-Server
/// <c>TaskQueueListRepositoryTests</c> (not runnable in this sandbox). Here, <see
/// cref="ITaskQueueRepository"/> is mocked with canned rows/totalCount and these tests instead prove:
/// (a) validation runs before any repository call and rejects out-of-range input (V1-V3), and (b) the
/// service passes <c>status</c>/<c>page</c>/<c>pageSize</c>/<c>actingUserId</c> through to
/// <see cref="ITaskQueueRepository.ListAsync"/> unchanged and correctly maps whatever rows the mocked
/// repository returns into <see cref="TaskListItem"/> (SC-1, SC-2).
/// </summary>
public sealed class TaskListServiceTests
{
    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public ILogger<TaskListService> Logger { get; } = Substitute.For<ILogger<TaskListService>>();

        public TaskListService CreateSut() => new(TaskQueueRepository, Logger);
    }

    private static TaskQueueListRow Row(
        int taskQueueId, string taskType, string submittedBy, DateTimeOffset submittedAt, string status) => new()
        {
            TaskQueueId = taskQueueId,
            TaskType = taskType,
            SubmittedByUserId = 7,
            Username = submittedBy,
            SubmittedAt = submittedAt,
            Status = status
        };

    // ---- SC-1: own tasks, newest-first, correctly paginated (repository mocked) ---------------------

    [Fact]
    public async Task Test_SC1_ListReturnsOwnTasksNewestFirstPaginated()
    {
        // Arrange — dbo.task_queue_lst is the layer that enforces "own rows only, newest first,
        // paginated" ordering; here it returns exactly the two rows it is contracted to, already
        // ordered submitted_at DESC, scoped to actingUserId.
        var fixture = new Fixture();
        var newest = Row(100234, "KeywordSearch", "m.alvarez",
            new DateTimeOffset(2026, 9, 24, 14, 5, 3, TimeSpan.Zero), "Running");
        var older = Row(100200, "ContentScan", "m.alvarez",
            new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero), "Completed");
        fixture.TaskQueueRepository
            .ListAsync(7, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<TaskQueueListRow> { newest, older }, 2));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(status: null, page: 1, pageSize: 50, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskListOutcome.Found);
        result.Response!.Items.Should().HaveCount(2);
        result.Response.Items[0].Should().BeEquivalentTo(new TaskListItem
        {
            TaskId = 100234,
            Type = "KeywordSearch",
            SubmittedBy = "m.alvarez",
            SubmittedAt = newest.SubmittedAt,
            StartTime = newest.SubmittedAt,
            Status = "Running",
            IsAcknowledged = false
        });
        result.Response.Items[1].TaskId.Should().Be(100200);
        result.Response.Page.Should().Be(1);
        result.Response.PageSize.Should().Be(50);
        result.Response.TotalCount.Should().Be(2);
        await fixture.TaskQueueRepository.Received(1).ListAsync(7, null, null, 1, 50, Arg.Any<CancellationToken>());
    }

    // ---- SC-2: status filter passes through unchanged to the repository ------------------------------

    [Fact]
    public async Task Test_SC2_ListFiltersByStatus()
    {
        // Arrange — dbo.task_queue_lst enforces status = 'Failed' server-side; here the mock
        // repository returns only a Failed row, proving the service passes status through unchanged
        // and does not re-filter or widen what the repository already filtered.
        var fixture = new Fixture();
        var failedRow = Row(100501, "SyncCopy", "m.alvarez",
            new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero), "Failed");
        fixture.TaskQueueRepository
            .ListAsync(7, null, "Failed", 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<TaskQueueListRow> { failedRow }, 1));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(status: "Failed", page: 1, pageSize: 50, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Response!.Items.Should().ContainSingle(i => i.Status == "Failed");
        await fixture.TaskQueueRepository.Received(1).ListAsync(7, null, "Failed", 1, 50, Arg.Any<CancellationToken>());
    }

    // ---- Code-review regression (2026-09-24): empty-string status normalized to null -----------------

    [Fact]
    public async Task Test_ListNormalizesEmptyStringStatusToNull()
    {
        // Arrange — an empty-string status passes Validate()'s !IsNullOrEmpty check (same as null
        // does), so without normalization it would reach dbo.task_queue_lst's own
        // @status IS NOT NULL AND @status NOT IN (...) guard and RAISERROR as an unhandled 500
        // instead of being treated as "no filter."
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .ListAsync(7, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<TaskQueueListRow>(), 0));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(status: "", page: 1, pageSize: 50, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskListOutcome.Found);
        await fixture.TaskQueueRepository.Received(1).ListAsync(7, null, null, 1, 50, Arg.Any<CancellationToken>());
    }

    // ---- V-1: unrecognized status rejected before any repository call --------------------------------

    [Fact]
    public async Task Test_V1_ListRejectsUnrecognizedStatus()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(status: "NotARealStatus", page: 1, pageSize: 50, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskListOutcome.ValidationFailed);
        result.ValidationError.Should().NotBeNullOrEmpty();
        await fixture.TaskQueueRepository.DidNotReceive().ListAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V-2: page must be >= 1 -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Test_V2_ListRejectsPageBelowOne(int page)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(status: null, page: page, pageSize: 50, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskListOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().ListAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V-3: pageSize must be between 1 and 200 (CAS-009's MaxPageSize precedent) --------------------

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Test_V3_ListRejectsPageSizeOutOfRange(int pageSize)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(status: null, page: 1, pageSize: pageSize, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskListOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().ListAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
