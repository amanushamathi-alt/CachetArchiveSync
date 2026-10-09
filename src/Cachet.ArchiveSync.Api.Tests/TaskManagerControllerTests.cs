using System.Security.Claims;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Direct, controller-level unit tests for the CAS-030 §6 additions to
/// <see cref="TaskManagerController"/> — CAS-030 §13 "TaskManagerControllerTests (unit)": V1, V2.
/// Same construction pattern as <see cref="SearchControllerTests"/> (claims-bearing
/// <see cref="DefaultHttpContext"/> + <see cref="TestProblemDetailsFactory"/>);
/// <see cref="ITaskManagerService"/> is an NSubstitute fake, so only the controller's own
/// validation/branching is under test. The full HTTP round trip (routing, auth, LocalDB) is proved in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.TaskManagerControllerTests</c>.
/// </summary>
public sealed class TaskManagerControllerTests
{
    private const string ValidationFailedType = "https://cachet.tracecounts.internal/errors/validation-failed";
    private const string TaskNotFoundType = "https://cachet.tracecounts.internal/errors/task-not-found";

    private sealed class Fixture
    {
        public ITaskManagerService TaskManagerService { get; } = Substitute.For<ITaskManagerService>();

        public TaskManagerController CreateSut(int? userId = 9, string? username = "m.alvarez") =>
            new(TaskManagerService)
            {
                ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(userId, username) }
            };

        private static DefaultHttpContext CreateHttpContext(int? userId, string? username)
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

            if (username is not null)
            {
                claims.Add(new Claim(ClaimTypes.Name, username));
            }

            return new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            };
        }
    }

    private static PagedResult<TaskListItem> EmptyPage() => new()
    {
        Items = Array.Empty<TaskListItem>(),
        Page = 1,
        PageSize = 50,
        TotalCount = 0
    };

    private static void AssertProblem(IActionResult result, int statusCode, string type)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(statusCode);
        objectResult.Value.Should().BeOfType<ProblemDetails>().Which.Type.Should().Be(type);
    }

    // ---- V1: page >= 1; pageSize 1-100 --------------------------------------------------------------

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Test_V1_ListTasks_InvalidPaging_Returns400(int page, int pageSize)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.List(type: null, status: null, page, pageSize, CancellationToken.None);

        // Assert
        AssertProblem(result, StatusCodes.Status400BadRequest, ValidationFailedType);
        await fixture.TaskManagerService.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V2: type/status must be one of the §6 values (exact, case-sensitive) -------------------------

    [Theory]
    [InlineData("keywordsearch", null)]
    [InlineData("Report", null)]
    [InlineData("", null)]
    [InlineData(null, "completed")]
    [InlineData(null, "Done")]
    public async Task Test_V2_ListTasks_UnknownTypeOrStatus_Returns400(string? type, string? status)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.List(type, status, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        AssertProblem(result, StatusCodes.Status400BadRequest, ValidationFailedType);
        await fixture.TaskManagerService.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- Coverage-gap closure (not a spec-mapped SC/V ID; same precedent as SearchControllerTests):
    //      the valid-value boundary, the claim-less 401 branches, and the outcome -> status mapping.

    [Theory]
    [InlineData("KeywordSearch", "Queued", 1, 1)]
    [InlineData("ContentScan", "Running", 1, 100)]
    [InlineData("SyncCopy", "Completed", 3, 50)]
    [InlineData("SyncUpdate", "Failed", 1, 50)]
    [InlineData("SyncPurge", "Cancelled", 1, 50)]
    [InlineData(null, null, 1, 50)]
    public async Task Test_ListTasks_ValidArguments_Returns200FromService(string? type, string? status, int page, int pageSize)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskManagerService.ListAsync(9, type, status, page, pageSize, Arg.Any<CancellationToken>())
            .Returns(EmptyPage());
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.List(type, status, page, pageSize, CancellationToken.None);

        // Assert — the user id passed to the service is the claim-derived one, never a request value.
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PagedResult<TaskListItem>>();
        await fixture.TaskManagerService.Received(1).ListAsync(9, type, status, page, pageSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_AllCas030Actions_NoUserIdClaim_Return401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);

        // Act + Assert
        (await sut.List(null, null, 1, 50, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await sut.Summary(CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await sut.Get(100234, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        (await sut.Acknowledge(100234, CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();
        fixture.TaskManagerService.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Test_GetTask_NoUsernameClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: 9, username: null);

        // Act
        var result = await sut.Get(100234, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Test_Summary_Returns200WithServiceCounts()
    {
        // Arrange
        var fixture = new Fixture();
        var summary = new TaskSummaryResponse { ActiveCount = 1, UnacknowledgedTerminalCount = 2 };
        fixture.TaskManagerService.GetSummaryAsync(9, Arg.Any<CancellationToken>()).Returns(summary);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Summary(CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(summary);
    }

    [Fact]
    public async Task Test_GetTask_FoundReturns200_NullReturns404()
    {
        // Arrange
        var fixture = new Fixture();
        var detail = new TaskDetailResponse
        {
            TaskId = 100234,
            Type = "KeywordSearch",
            SubmittedBy = "m.alvarez",
            SubmittedAt = DateTimeOffset.UtcNow,
            Status = "Completed",
            IsAcknowledged = false
        };
        fixture.TaskManagerService.GetAsync(100234, 9, "m.alvarez", Arg.Any<CancellationToken>()).Returns(detail);
        fixture.TaskManagerService.GetAsync(999999, 9, "m.alvarez", Arg.Any<CancellationToken>()).Returns((TaskDetailResponse?)null);
        var sut = fixture.CreateSut();

        // Act
        var found = await sut.Get(100234, CancellationToken.None);
        var missing = await sut.Get(999999, CancellationToken.None);

        // Assert
        found.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(detail);
        AssertProblem(missing, StatusCodes.Status404NotFound, TaskNotFoundType);
    }

    [Theory]
    [InlineData(AcknowledgeTaskOutcome.Acknowledged, StatusCodes.Status204NoContent, null)]
    [InlineData(AcknowledgeTaskOutcome.NotFound, StatusCodes.Status404NotFound, TaskNotFoundType)]
    [InlineData(AcknowledgeTaskOutcome.NotTerminal, StatusCodes.Status409Conflict, "https://cachet.tracecounts.internal/errors/task-not-terminal")]
    public async Task Test_Acknowledge_MapsOutcomeToStatus(AcknowledgeTaskOutcome outcome, int expectedStatus, string? expectedType)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskManagerService.AcknowledgeAsync(100234, 9, Arg.Any<CancellationToken>()).Returns(outcome);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Acknowledge(100234, CancellationToken.None);

        // Assert
        if (expectedType is null)
        {
            result.Should().BeOfType<NoContentResult>().Which.StatusCode.Should().Be(expectedStatus);
        }
        else
        {
            AssertProblem(result, expectedStatus, expectedType);
        }
    }
}
