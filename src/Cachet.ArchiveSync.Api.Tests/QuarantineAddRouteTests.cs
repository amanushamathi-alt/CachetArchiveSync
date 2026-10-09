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
/// Controller-level proof that <c>POST /quarantine</c> (<see cref="QuarantineController.Add"/>) is a
/// true route alias over the exact same <see cref="IQuarantineService.AddAsync"/> call
/// <see cref="SearchController.AddToQuarantine"/> already makes (CAS-008 §0/§6, §7 V8, §10 SC-2) —
/// not a second, divergent implementation of add logic. The smallest correct proof of route-alias
/// equivalence is a controller unit test verifying delegation (NSubstitute verify), per this
/// project's stated preference for the smallest correct proof — spinning up both routes end-to-end
/// against two separate controllers is unnecessary given both call sites invoke the identical
/// service method with the identical outcome-to-status-code mapping (compare this class's assertions
/// against <see cref="SearchControllerTests"/>'s <c>Test_AddToQuarantine_*</c> tests).
/// </summary>
public sealed class QuarantineAddRouteTests
{
    private sealed class Fixture
    {
        public IQuarantineListService QuarantineListService { get; } = Substitute.For<IQuarantineListService>();
        public IQuarantineService QuarantineService { get; } = Substitute.For<IQuarantineService>();
        public IQuarantinePermanentDeleteService QuarantinePermanentDeleteService { get; } = Substitute.For<IQuarantinePermanentDeleteService>();
        public IQuarantineRemoveService QuarantineRemoveService { get; } = Substitute.For<IQuarantineRemoveService>();
        public IQuarantineBrowseService QuarantineBrowseService { get; } = Substitute.For<IQuarantineBrowseService>();

        public QuarantineController CreateSut(int? userId = 1)
        {
            var controller = new QuarantineController(QuarantineListService, QuarantineService, QuarantinePermanentDeleteService, QuarantineRemoveService, QuarantineBrowseService)
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

    // ---- V8: POST /quarantine delegates to the exact same IQuarantineService.AddAsync -------------

    [Fact]
    public async Task Test_V8_PostQuarantineRouteDelegatesToSameServiceAsSearchResultsQuarantine()
    {
        // Arrange
        var fixture = new Fixture();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };
        var response = new QuarantineAddResponse { Items = new List<QuarantineAddResponseItem>() };
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = QuarantineAddOutcome.Added, Response = response });
        var sut = fixture.CreateSut();

        // Act
        await sut.Add(request, CancellationToken.None);

        // Assert — the exact request object received by the controller is passed through unchanged
        // to IQuarantineService.AddAsync, the same method SearchController.AddToQuarantine calls; no
        // new/parallel validation or add logic exists on this route (§0, §4).
        await fixture.QuarantineService.Received(1).AddAsync(request, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V8_NoUserIdClaim_Returns401BeforeDelegation()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.Add(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantineService.DidNotReceive().AddAsync(Arg.Any<QuarantineAddRequest>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-2: identical response shape/status-code mapping to POST /search/results/quarantine -----

    [Fact]
    public async Task Test_SC2_PostQuarantineAliasProducesSameResultAsExistingEndpoint()
    {
        // Arrange — the exact same QuarantineAddResult a valid add produces.
        var fixture = new Fixture();
        var response = new QuarantineAddResponse
        {
            Items = new List<QuarantineAddResponseItem>
            {
                new()
                {
                    FileId = 100801,
                    Name = "agreement-004.pdf",
                    Path = "contracts/2019/agreement-004.pdf",
                    QuarantinedOn = DateTimeOffset.UtcNow,
                    Status = "Flagged"
                }
            }
        };
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = QuarantineAddOutcome.Added, Response = response });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.Add(request, CancellationToken.None);

        // Assert — identical 200 + response body shape to SearchController.AddToQuarantine's own
        // Added-outcome branch (compare SearchControllerTests.Test_AddToQuarantine_Added_Returns200).
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    [Theory]
    [InlineData(QuarantineAddOutcome.ValidationFailed, StatusCodes.Status422UnprocessableEntity, "'fileIds' must be non-empty.")]
    [InlineData(QuarantineAddOutcome.TaskNotFound, StatusCodes.Status404NotFound, "Task not found.")]
    [InlineData(QuarantineAddOutcome.FileNotFound, StatusCodes.Status404NotFound, "One or more fileIds do not belong to this task.")]
    [InlineData(QuarantineAddOutcome.AllAlreadyQuarantined, StatusCodes.Status409Conflict, "All requested files are already actively quarantined.")]
    public async Task Test_SC2_ErrorOutcomesMapToSameStatusCodeAndTitleAsExistingEndpoint(
        QuarantineAddOutcome outcome, int expectedStatusCode, string expectedTitle)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineService
            .AddAsync(Arg.Any<QuarantineAddRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineAddResult { Outcome = outcome });
        var sut = fixture.CreateSut();
        var request = new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } };

        // Act
        var result = await sut.Add(request, CancellationToken.None);

        // Assert — same error mapping as SearchController.AddToQuarantine's identical switch (§0, §6).
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatusCode);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be(expectedTitle);
    }
}
