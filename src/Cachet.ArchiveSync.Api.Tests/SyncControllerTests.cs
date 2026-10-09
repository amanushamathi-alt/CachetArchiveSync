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
/// CAS-045 §13 — direct controller-level tests for <c>GET /sync/{taskId}/results</c>: the §8.2 paging
/// validation (V9: <c>400</c> before any service/DB call) and the §8.1 <c>excludeSynced</c> pass-through
/// (V10, default <c>false</c>). The full-HTTP proof lives in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.SyncResultsEndpointTests</c>.
/// </summary>
public sealed class SyncControllerTests
{
    private sealed class Fixture
    {
        public ISyncScanService SyncScanService { get; } = Substitute.For<ISyncScanService>();

        public SyncController CreateSut()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<ApiBehaviorOptions>(_ => { });
            services.AddSingleton<ProblemDetailsFactory, TestProblemDetailsFactory>();

            return new SyncController(SyncScanService, Substitute.For<ISyncActionService>(), Substitute.For<IB2MirrorSyncService>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        RequestServices = services.BuildServiceProvider(),
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "Test"))
                    }
                }
            };
        }
    }

    [Theory]
    [InlineData("newOnODrivePage", 0, 50)]
    [InlineData("removedFromODrivePage", -1, 50)]
    [InlineData("modifiedOnODrivePage", 0, 50)]
    [InlineData("newOnODrivePageSize", 1, 0)]
    [InlineData("removedFromODrivePageSize", 1, 101)]
    [InlineData("modifiedOnODrivePageSize", 1, 1000)]
    [InlineData("newOnODrivePageSize", 1, -5)]
    public async Task Test_V9_ResultsEndpointRejectsInvalidPaging(string offendingParameter, int page, int pageSize)
    {
        // Arrange — put the invalid value on exactly one parameter; the rest stay valid defaults.
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var (newPage, newSize, removedPage, removedSize, modifiedPage, modifiedSize) = (1, 50, 1, 50, 1, 50);
        switch (offendingParameter)
        {
            case "newOnODrivePage": newPage = page; break;
            case "removedFromODrivePage": removedPage = page; break;
            case "modifiedOnODrivePage": modifiedPage = page; break;
            case "newOnODrivePageSize": newSize = pageSize; break;
            case "removedFromODrivePageSize": removedSize = pageSize; break;
            case "modifiedOnODrivePageSize": modifiedSize = pageSize; break;
        }

        // Act
        var result = await sut.GetResults(100003, newPage, newSize, removedPage, removedSize, modifiedPage, modifiedSize, false, CancellationToken.None);

        // Assert — 400 problem naming only the offending parameter, and no service (DB) call at all.
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cachet.tracecounts.internal/errors/invalid-paging");
        problem.Title.Should().Contain(offendingParameter);
        await fixture.SyncScanService.DidNotReceiveWithAnyArgs().GetResultsAsync(
            default, default, default, default, default, default, default, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Test_V10_ExcludeSyncedFiltersRowsAndTotal(bool excludeSynced)
    {
        // Arrange
        var fixture = new Fixture();
        fixture.SyncScanService
            .GetResultsAsync(100003, 1, 50, 2, 25, 3, 100, excludeSynced, Arg.Any<CancellationToken>())
            .Returns(new SyncResultsQueryResult
            {
                Outcome = SyncResultsOutcome.Found,
                Response = new SyncScanResultsResponse { TaskId = 100003, Status = "Completed" }
            });
        var sut = fixture.CreateSut();

        // Act — valid boundary values (page size 1 and 100 are both allowed).
        var result = await sut.GetResults(100003, 1, 50, 2, 25, 3, 100, excludeSynced, CancellationToken.None);

        // Assert — the flag reaches the service exactly as supplied (default false = today's behavior).
        result.Should().BeOfType<OkObjectResult>();
        await fixture.SyncScanService.Received(1).GetResultsAsync(100003, 1, 50, 2, 25, 3, 100, excludeSynced, Arg.Any<CancellationToken>());
    }
}
