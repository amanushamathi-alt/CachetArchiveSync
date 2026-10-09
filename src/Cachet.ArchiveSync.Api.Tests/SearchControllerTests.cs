using System.Security.Claims;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Minimal <see cref="ProblemDetailsFactory"/> for controller unit tests — the framework's real
/// <c>DefaultProblemDetailsFactory</c> is internal, so <see cref="ControllerBase.Problem"/> needs
/// something registered in <c>HttpContext.RequestServices</c> to resolve outside a real host.
/// </summary>
internal sealed class TestProblemDetailsFactory : ProblemDetailsFactory
{
    public override ProblemDetails CreateProblemDetails(
        HttpContext httpContext,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null) => new()
        {
            Status = statusCode ?? StatusCodes.Status500InternalServerError,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance
        };

    public override ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext,
        ModelStateDictionary modelStateDictionary,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null) => new(modelStateDictionary)
        {
            Status = statusCode ?? StatusCodes.Status400BadRequest,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = instance
        };
}

/// <summary>
/// Direct, controller-level unit tests for <see cref="SearchController"/> (CAS-006 §6) — a
/// coverage top-up over <c>Cachet.ArchiveSync.Api.IntegrationTests.SearchControllerTests</c>'s
/// full-HTTP-round-trip proof, which does not feed this project's unit-test coverage gate.
/// <see cref="IKeywordSearchService"/>/<see cref="IQuarantineService"/> are both NSubstitute
/// fakes — the controller's own branching (outcome switch -> status code, <c>GetUserId()</c>) is
/// what's under test here, not the services' logic (covered in
/// <see cref="KeywordSearchServiceTests"/>/<see cref="QuarantineServiceTests"/>).
/// </summary>
public sealed class SearchControllerTests
{
    private sealed class Fixture
    {
        public IKeywordSearchService KeywordSearchService { get; } = Substitute.For<IKeywordSearchService>();
        public IQuarantineService QuarantineService { get; } = Substitute.For<IQuarantineService>();

        public SearchController CreateSut(int? userId = 1)
        {
            var controller = new SearchController(KeywordSearchService, QuarantineService)
            {
                ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(userId) }
            };
            return controller;
        }

        // Problem() resolves a ProblemDetailsFactory from HttpContext.RequestServices — without a
        // registered one, Problem() throws NullReferenceException outside a real ASP.NET Core host.
        private static DefaultHttpContext CreateHttpContext(int? userId)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<ApiBehaviorOptions>(_ => { });
            services.AddSingleton<ProblemDetailsFactory, TestProblemDetailsFactory>();

            var identity = userId is null
                ? new ClaimsIdentity(authenticationType: "Test") // no NameIdentifier claim
                : new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test");

            return new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = new ClaimsPrincipal(identity)
            };
        }
    }

    private static SearchRequest ValidSearchRequest() => new() { Term = "confidential", Bucket = "cachet-archive-prod", Extension = "All" };

    // ---- POST /search -----------------------------------------------------------------------------

    [Fact]
    public async Task Test_Submit_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);

        // Act
        var result = await sut.Submit(ValidSearchRequest(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.KeywordSearchService.DidNotReceive().SubmitAsync(Arg.Any<SearchRequest>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_Submit_ValidationFailed_Returns422()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.KeywordSearchService
            .SubmitAsync(Arg.Any<SearchRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new SearchSubmitResult { Outcome = SearchSubmitOutcome.ValidationFailed, ValidationError = "'term' is required." });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Submit(ValidSearchRequest(), CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("'term' is required.");
    }

    [Fact]
    public async Task Test_Submit_Accepted_Returns202()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new SearchSubmittedResponse { TaskId = 100234, Type = "KeywordSearch", Status = "Queued", SubmittedAt = DateTimeOffset.UtcNow };
        fixture.KeywordSearchService
            .SubmitAsync(Arg.Any<SearchRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new SearchSubmitResult { Outcome = SearchSubmitOutcome.Accepted, Response = response });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Submit(ValidSearchRequest(), CancellationToken.None);

        // Assert
        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Value.Should().Be(response);
    }

    // ---- GET /search/{taskId}/results --------------------------------------------------------------

    [Fact]
    public async Task Test_GetResults_TaskNotFound_Returns404()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.KeywordSearchService
            .GetResultsAsync(999, 1, 50, Arg.Any<CancellationToken>())
            .Returns(new SearchResultsQueryResult { Outcome = SearchResultsOutcome.TaskNotFound });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResults(999, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Test_GetResults_TaskNotComplete_Returns409()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.KeywordSearchService
            .GetResultsAsync(100234, 1, 50, Arg.Any<CancellationToken>())
            .Returns(new SearchResultsQueryResult { Outcome = SearchResultsOutcome.TaskNotComplete });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResults(100234, page: 1, pageSize: 50, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Test_GetResults_Found_Returns200()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new SearchResultsResponse { TaskId = 100234, Status = "Completed" };
        fixture.KeywordSearchService
            .GetResultsAsync(100234, 2, 25, Arg.Any<CancellationToken>())
            .Returns(new SearchResultsQueryResult { Outcome = SearchResultsOutcome.Found, Response = response });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResults(100234, page: 2, pageSize: 25, CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    // ---- POST /search/results/quarantine -------------------------------------------------------------

    [Fact]
    public async Task Test_AddToQuarantine_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddToQuarantine(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantineService.DidNotReceive().AddAsync(Arg.Any<QuarantineAddRequest>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_AddToQuarantine_ValidationFailed_Returns422()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = QuarantineAddOutcome.ValidationFailed });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = Array.Empty<int>() };

        // Act
        var result = await sut.AddToQuarantine(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task Test_AddToQuarantine_TaskNotFound_Returns404()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = QuarantineAddOutcome.TaskNotFound });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 999, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddToQuarantine(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("Task not found.");
    }

    [Fact]
    public async Task Test_AddToQuarantine_FileNotFound_Returns404()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = QuarantineAddOutcome.FileNotFound });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 999999 } };

        // Act
        var result = await sut.AddToQuarantine(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("One or more fileIds do not belong to this task.");
    }

    [Fact]
    public async Task Test_AddToQuarantine_AllAlreadyQuarantined_Returns409()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = QuarantineAddOutcome.AllAlreadyQuarantined });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddToQuarantine(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Test_AddToQuarantine_Added_Returns200()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new QuarantineAddResponse { Items = new List<QuarantineAddResponseItem>() };
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = QuarantineAddOutcome.Added, Response = response });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.AddToQuarantine(request, CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }
}
