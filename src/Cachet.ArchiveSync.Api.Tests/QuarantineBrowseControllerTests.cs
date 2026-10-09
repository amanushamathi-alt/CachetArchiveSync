using System.Reflection;
using System.Security.Claims;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Controller-level unit tests for the CAS-050 actions of <see cref="QuarantineController"/>:
/// <c>POST /quarantine/browse</c>, <c>POST /quarantine/browse/remove</c>, <c>GET /quarantine/browse/state</c>
/// (CAS-050 §8, §13 "QuarantineBrowseControllerTests": policy 403/200, status mapping, request validation).
/// ASP.NET Core policy enforcement runs above the action, so the 403 itself is proven end-to-end by
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.QuarantineControllerTests</c>; here the <c>[Authorize]</c>
/// attributes are asserted via reflection and the outcome-to-status mapping by direct invocation.
/// </summary>
public sealed class QuarantineBrowseControllerTests
{
    private sealed class Fixture
    {
        public IQuarantineListService QuarantineListService { get; } = Substitute.For<IQuarantineListService>();
        public IQuarantineService QuarantineService { get; } = Substitute.For<IQuarantineService>();
        public IQuarantinePermanentDeleteService QuarantinePermanentDeleteService { get; } = Substitute.For<IQuarantinePermanentDeleteService>();
        public IQuarantineRemoveService QuarantineRemoveService { get; } = Substitute.For<IQuarantineRemoveService>();
        public IQuarantineBrowseService QuarantineBrowseService { get; } = Substitute.For<IQuarantineBrowseService>();

        public QuarantineController CreateSut(int? userId = 1) =>
            new(QuarantineListService, QuarantineService, QuarantinePermanentDeleteService, QuarantineRemoveService, QuarantineBrowseService)
            {
                ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(userId) }
            };

        private static DefaultHttpContext CreateHttpContext(int? userId)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<ApiBehaviorOptions>(_ => { });
            services.AddSingleton<ProblemDetailsFactory, TestProblemDetailsFactory>();

            var identity = userId is null
                ? new ClaimsIdentity(authenticationType: "Test")
                : new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test");

            return new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = new ClaimsPrincipal(identity)
            };
        }
    }

    private static string PolicyOf(Type controller, string action) =>
        controller.GetMethod(action)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Single()
            .Policy!;

    private static QuarantineBrowseRequest FolderRequest() =>
        new() { Scope = "Folder", Source = "B2", BucketName = "bkt", FolderPath = "Ops" };

    private static ObjectResult AssertProblem(IActionResult result, int status, string errorTypeSuffix)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(status);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().EndWith("/errors/" + errorTypeSuffix);
        return objectResult;
    }

    // ---- V1: request validation -> 422 ProblemDetails (add, remove, state) -------------------------------

    [Fact]
    public async Task Test_V1_Browse_InvalidShape_422()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineBrowseService
            .AddAsync(Arg.Any<QuarantineBrowseRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.ValidationFailed });
        fixture.QuarantineBrowseService
            .RemoveAsync(Arg.Any<QuarantineBrowseRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.ValidationFailed });
        fixture.QuarantineBrowseService
            .GetStateAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseStateResult { Outcome = QuarantineBrowseStateOutcome.ValidationFailed });
        var sut = fixture.CreateSut();
        var bad = new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "bkt", FolderPath = "/Ops/" };

        // Act
        var add = await sut.BrowseAdd(bad, CancellationToken.None);
        var remove = await sut.BrowseRemove(bad, CancellationToken.None);
        var state = await sut.BrowseState("Nope", "bkt", "Ops", CancellationToken.None);

        // Assert
        AssertProblem(add, StatusCodes.Status422UnprocessableEntity, "validation-failed");
        AssertProblem(remove, StatusCodes.Status422UnprocessableEntity, "validation-failed");
        AssertProblem(state, StatusCodes.Status422UnprocessableEntity, "validation-failed");
    }

    // ---- V2: both browse write routes require Full Access at action level (Read Only never reaches service) --

    [Fact]
    public void Test_V2_BrowseWrites_ReadOnly_403()
    {
        // The [Authorize(Policy = "CachetFullAccess")] gate rejects Read Only sessions with 403 before the
        // action (and therefore the service) runs; the live 403 + "service never called" is proven by
        // IntegrationTests.QuarantineControllerTests.Test_V11_AllQuarantineWrites_ReadOnly_403.
        PolicyOf(typeof(QuarantineController), nameof(QuarantineController.BrowseAdd)).Should().Be("CachetFullAccess");
        PolicyOf(typeof(QuarantineController), nameof(QuarantineController.BrowseRemove)).Should().Be("CachetFullAccess");
    }

    // ---- SC-6: Read Only 403 on both write routes, state/list stay CachetAuthenticated --------------------

    [Fact]
    public void Test_SC6_ReadOnly()
    {
        PolicyOf(typeof(QuarantineController), nameof(QuarantineController.BrowseAdd)).Should().Be("CachetFullAccess");
        PolicyOf(typeof(QuarantineController), nameof(QuarantineController.BrowseRemove)).Should().Be("CachetFullAccess");
        PolicyOf(typeof(QuarantineController), nameof(QuarantineController.BrowseState)).Should().Be("CachetAuthenticated");
        PolicyOf(typeof(QuarantineController), nameof(QuarantineController.GetActive)).Should().Be("CachetAuthenticated");
    }

    // ---- status mapping: browse add --------------------------------------------------------------------

    [Fact]
    public async Task Test_BrowseAdd_Completed_Returns200WithBody()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new QuarantineBrowseAddResponse { Added = 3, AlreadyQuarantined = 1 };
        var request = FolderRequest();
        fixture.QuarantineBrowseService
            .AddAsync(request, 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.Completed, Response = response });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.BrowseAdd(request, CancellationToken.None);

        // Assert - exact request passed through; response returned as-is.
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().Be(response);
        await fixture.QuarantineBrowseService.Received(1).AddAsync(request, 1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(QuarantineBrowseAddOutcome.FileNotFound, 404, "file-not-found")]
    [InlineData(QuarantineBrowseAddOutcome.FolderNotFound, 404, "folder-not-found")]
    [InlineData(QuarantineBrowseAddOutcome.AlreadyQuarantined, 409, "already-quarantined")]
    public async Task Test_BrowseAdd_OutcomeMapsToStatus(QuarantineBrowseAddOutcome outcome, int status, string errorType)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineBrowseService
            .AddAsync(Arg.Any<QuarantineBrowseRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseAddResult { Outcome = outcome });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.BrowseAdd(FolderRequest(), CancellationToken.None);

        // Assert
        AssertProblem(result, status, errorType);
    }

    // ---- CAS-053 SC-6 (HTTP): missing mirror mapping -> 500 on all three routes -------------------------

    [Fact]
    public async Task Test_SC6_MappingMissing_500()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineBrowseService
            .AddAsync(Arg.Any<QuarantineBrowseRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseAddResult { Outcome = QuarantineBrowseAddOutcome.MappingMissing });
        fixture.QuarantineBrowseService
            .RemoveAsync(Arg.Any<QuarantineBrowseRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.MappingMissing });
        fixture.QuarantineBrowseService
            .GetStateAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseStateResult { Outcome = QuarantineBrowseStateOutcome.MappingMissing });
        var sut = fixture.CreateSut();

        // Act
        var add = await sut.BrowseAdd(FolderRequest(), CancellationToken.None);
        var remove = await sut.BrowseRemove(FolderRequest(), CancellationToken.None);
        var state = await sut.BrowseState("B2", "bkt", "Ops", CancellationToken.None);

        // Assert
        AssertProblem(add, StatusCodes.Status500InternalServerError, "mirror-mapping-missing");
        AssertProblem(remove, StatusCodes.Status500InternalServerError, "mirror-mapping-missing");
        AssertProblem(state, StatusCodes.Status500InternalServerError, "mirror-mapping-missing");
    }

    [Fact]
    public async Task Test_BrowseAdd_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);

        // Act
        var result = await sut.BrowseAdd(FolderRequest(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantineBrowseService.DidNotReceiveWithAnyArgs().AddAsync(default!, default, default);
    }

    // ---- status mapping: browse remove -----------------------------------------------------------------

    [Fact]
    public async Task Test_BrowseRemove_Completed_Returns200WithBody()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new QuarantineBrowseRemoveResponse { Removed = 4 };
        fixture.QuarantineBrowseService
            .RemoveAsync(Arg.Any<QuarantineBrowseRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseRemoveResult { Outcome = QuarantineBrowseRemoveOutcome.Completed, Response = response });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.BrowseRemove(FolderRequest(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().Be(response);
    }

    [Theory]
    [InlineData(QuarantineBrowseRemoveOutcome.NotQuarantined, 404, "not-quarantined")]
    [InlineData(QuarantineBrowseRemoveOutcome.FolderNotFound, 404, "folder-not-found")]
    public async Task Test_BrowseRemove_OutcomeMapsToStatus(QuarantineBrowseRemoveOutcome outcome, int status, string errorType)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineBrowseService
            .RemoveAsync(Arg.Any<QuarantineBrowseRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseRemoveResult { Outcome = outcome });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.BrowseRemove(FolderRequest(), CancellationToken.None);

        // Assert
        AssertProblem(result, status, errorType);
    }

    [Fact]
    public async Task Test_BrowseRemove_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);

        // Act
        var result = await sut.BrowseRemove(FolderRequest(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantineBrowseService.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default, default);
    }

    // ---- status mapping: state --------------------------------------------------------------------------

    [Fact]
    public async Task Test_BrowseState_Completed_Returns200WithBody()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new QuarantineBrowseStateResponse
        {
            FileIndexIds = new[] { 1 },
            Folders = new[] { new QuarantineBrowseStateFolder { FolderName = "Ops", TotalFiles = 2, QuarantinedFiles = 1 } }
        };
        fixture.QuarantineBrowseService
            .GetStateAsync("B2", "bkt", "Ops", Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseStateResult { Outcome = QuarantineBrowseStateOutcome.Completed, Response = response });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.BrowseState("B2", "bkt", "Ops", CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().Be(response);
    }

    [Fact]
    public async Task Test_BrowseState_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);

        // Act
        var result = await sut.BrowseState("B2", "bkt", "Ops", CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantineBrowseService.DidNotReceiveWithAnyArgs().GetStateAsync(default, default, default, default);
    }
}
