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
/// Direct, controller-level unit tests for <see cref="QuarantineController"/>'s <c>GET /quarantine</c>
/// and <c>POST /quarantine/delete</c> actions (CAS-008 §6) — a coverage top-up over
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.QuarantineControllerTests</c>'s full-HTTP-round-trip
/// proof, which does not feed this project's unit-test coverage gate. <see cref="QuarantineAddRouteTests"/>
/// covers the third action (<c>POST /quarantine</c>, §0/§6, V8/SC-2). <see cref="IQuarantineListService"/>/
/// <see cref="IQuarantinePermanentDeleteService"/> are both NSubstitute fakes — the controller's own
/// branching (outcome switch -> status code, <c>GetUserId()</c>) is what's under test here, not the
/// services' logic (covered in <see cref="QuarantineListServiceTests"/>/
/// <see cref="QuarantinePermanentDeleteServiceTests"/>). CAS-014 adds <see cref="IQuarantineRemoveService"/>
/// (also an NSubstitute fake) and the <c>POST /quarantine/remove</c> route-level coverage below.
/// </summary>
public sealed class QuarantineControllerTests
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

    // ---- GET /quarantine ----------------------------------------------------------------------------

    [Fact]
    public async Task Test_GetActive_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);

        // Act
        var result = await sut.GetActive(CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantineListService.DidNotReceive().GetActiveAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_GetActive_ValidSession_Returns200WithItems()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new QuarantineListResponse
        {
            Items = new List<QuarantineListItem>
            {
                new()
                {
                    FileId = 100801,
                    FileIndexId = 600801,
                    Name = "agreement-004.pdf",
                    Path = "contracts/2019/agreement-004.pdf",
                    QuarantinedOn = DateTimeOffset.UtcNow,
                    Status = "Flagged",
                    SourceTab = "Keyword Search"
                }
            },
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        };
        fixture.QuarantineListService.GetActiveAsync(1, 1, 25, Arg.Any<CancellationToken>()).Returns(response);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetActive(CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    // ---- POST /quarantine/delete -----------------------------------------------------------------

    [Fact]
    public async Task Test_Delete_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.Delete(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantinePermanentDeleteService.DidNotReceive().DeleteAsync(
            Arg.Any<QuarantineDeleteRequest>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_Delete_ValidationFailed_Returns422()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantinePermanentDeleteService
            .DeleteAsync(Arg.Any<QuarantineDeleteRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.ValidationFailed });
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = Array.Empty<int>(), Confirm = true };

        // Act
        var result = await sut.Delete(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("'fileIds' must be non-empty.");
    }

    [Fact]
    public async Task Test_Delete_ConfirmationRequired_Returns422()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantinePermanentDeleteService
            .DeleteAsync(Arg.Any<QuarantineDeleteRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.ConfirmationRequired });
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = false };

        // Act
        var result = await sut.Delete(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("'confirm' must be true.");
    }

    [Fact]
    public async Task Test_Delete_AllFilesNotFound_Returns404()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantinePermanentDeleteService
            .DeleteAsync(Arg.Any<QuarantineDeleteRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.AllFilesNotFound });
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 999999 }, Confirm = true };

        // Act
        var result = await sut.Delete(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("None of the requested fileIds resolve to an active quarantine entry.");
    }

    [Fact]
    public async Task Test_Delete_Completed_Returns200()
    {
        // Arrange
        var fixture = new Fixture();
        var response = new QuarantineDeleteResponse
        {
            Results = new List<QuarantineDeleteResultItem> { new() { FileId = 100801, Result = "Success" } }
        };
        fixture.QuarantinePermanentDeleteService
            .DeleteAsync(Arg.Any<QuarantineDeleteRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.Completed, Response = response });
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.Delete(request, CancellationToken.None);

        // Assert
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    // ---- POST /quarantine/remove (CAS-014 §2.2/§8/§13) -----------------------------------------------
    // Route-level proof that POST /quarantine/remove maps to IQuarantineRemoveService.RemoveAsync and
    // requires CachetFullAccess (CAS-050 §11) — the [Authorize] attribute itself is asserted via
    // reflection here, since ASP.NET Core policy enforcement runs above the controller action and is
    // not otherwise observable from a direct unit-test call (the [Authorize(Policy=...)] gate is
    // exercised end-to-end by Cachet.ArchiveSync.Api.IntegrationTests.QuarantineControllerTests).

    [Fact]
    public void Test_Remove_RequiresCachetFullAccessPolicy()
    {
        // Arrange
        var method = typeof(QuarantineController).GetMethod(nameof(QuarantineController.Remove));

        // Act
        var authorizeAttribute = method!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Single();

        // Assert — CAS-050 §2.8/§11 (BRD v2.4 §12.5 item 8): tightened from CachetAuthenticated;
        // every Quarantine write now requires Full Access.
        authorizeAttribute.Policy.Should().Be("CachetFullAccess");
    }

    [Fact]
    public async Task Test_Remove_DelegatesToQuarantineRemoveService()
    {
        // Arrange
        var fixture = new Fixture();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 100801 } };
        var response = new QuarantineRemoveResponse
        {
            Results = new List<QuarantineRemoveResultItem> { new() { FileId = 100801, Result = "Success" } }
        };
        fixture.QuarantineRemoveService
            .RemoveAsync(Arg.Any<QuarantineRemoveRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineRemoveResult { Outcome = QuarantineRemoveOutcome.Completed, Response = response });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Remove(request, CancellationToken.None);

        // Assert — the exact request object is passed through, and the resulting response is
        // returned as-is (maps POST /quarantine/remove -> IQuarantineRemoveService.RemoveAsync).
        await fixture.QuarantineRemoveService.Received(1).RemoveAsync(request, 1, Arg.Any<CancellationToken>());
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(response);
    }

    [Fact]
    public async Task Test_Remove_NoUserIdClaim_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut(userId: null);
        var request = new QuarantineRemoveRequest { FileIds = new[] { 100801 } };

        // Act
        var result = await sut.Remove(request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.QuarantineRemoveService.DidNotReceive().RemoveAsync(
            Arg.Any<QuarantineRemoveRequest>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_Remove_ValidationFailed_Returns422()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineRemoveService
            .RemoveAsync(Arg.Any<QuarantineRemoveRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineRemoveResult { Outcome = QuarantineRemoveOutcome.ValidationFailed });
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = Array.Empty<int>() };

        // Act
        var result = await sut.Remove(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("'fileIds' must be non-empty.");
    }

    [Fact]
    public async Task Test_Remove_AllFilesNotFound_Returns404()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineRemoveService
            .RemoveAsync(Arg.Any<QuarantineRemoveRequest>(), 1, Arg.Any<CancellationToken>())
            .Returns(new QuarantineRemoveResult { Outcome = QuarantineRemoveOutcome.AllFilesNotFound });
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 999999 } };

        // Act
        var result = await sut.Remove(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Title.Should().Be("None of the requested fileIds resolve to an active quarantine entry.");
    }

    // ---- CAS-050 V7: GET /quarantine paging validation -------------------------------------------------

    [Theory]
    [InlineData(0, 25)]
    [InlineData(-3, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 10)]
    [InlineData(1, 26)]
    [InlineData(1, 100)]
    [InlineData(1_000_001, 25)]
    [InlineData(int.MaxValue, 50)]
    public async Task Test_V7_List_InvalidPaging_422(int page, int pageSize)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetActive(CancellationToken.None, page, pageSize);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        await fixture.QuarantineListService.DidNotReceiveWithAnyArgs().GetActiveAsync(default, default, default, default);
    }

    [Theory]
    [InlineData(1, 25)]
    [InlineData(2, 25)]
    [InlineData(1, 50)]
    [InlineData(7, 50)]
    [InlineData(1_000_000, 50)]   // upper boundary of the page cap (D1)
    public async Task Test_GetActive_ValidPaging_PassesPageAndPageSizeToService(int page, int pageSize)
    {
        // Arrange
        var fixture = new Fixture();
        var response = new QuarantineListResponse { Items = new List<QuarantineListItem>(), TotalCount = 0, Page = page, PageSize = pageSize };
        fixture.QuarantineListService.GetActiveAsync(1, page, pageSize, Arg.Any<CancellationToken>()).Returns(response);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetActive(CancellationToken.None, page, pageSize);

        // Assert
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().Be(response);
    }

    // ---- CAS-050 V11 (action-level supplement): all five write actions require CachetFullAccess ---------

    [Fact]
    public void Test_V11_AllFiveWriteActions_RequireCachetFullAccessAtActionLevel()
    {
        var writeActions = new[]
        {
            typeof(QuarantineController).GetMethod(nameof(QuarantineController.Add)),
            typeof(QuarantineController).GetMethod(nameof(QuarantineController.Remove)),
            typeof(QuarantineController).GetMethod(nameof(QuarantineController.BrowseAdd)),
            typeof(QuarantineController).GetMethod(nameof(QuarantineController.BrowseRemove)),
            typeof(SearchController).GetMethod(nameof(SearchController.AddToQuarantine))
        };

        foreach (var method in writeActions)
        {
            var policies = method!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(a => a.Policy)
                .ToList();

            policies.Should().Equal(new[] { "CachetFullAccess" }, $"{method.DeclaringType!.Name}.{method.Name} must be Full Access only");
        }
    }

    [Fact]
    public void Test_V11_ReadActions_RemainCachetAuthenticated()
    {
        foreach (var name in new[] { nameof(QuarantineController.GetActive), nameof(QuarantineController.BrowseState) })
        {
            typeof(QuarantineController).GetMethod(name)!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Single().Policy.Should().Be("CachetAuthenticated");
        }
    }
}
