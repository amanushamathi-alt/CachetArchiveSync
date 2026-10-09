using System.Security.Claims;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Controller-level unit tests for <see cref="AuthController"/> (CAS-002 §6.1/§6.3). Uses a real
/// <see cref="DefaultHttpContext"/> with a mocked <see cref="IAuthenticationService"/> resolved
/// from <c>HttpContext.RequestServices</c>, since <c>HttpContext.SignInAsync</c>/<c>SignOutAsync</c>
/// are extension methods that resolve that service internally — this lets these tests verify the
/// exact scheme/claims passed to sign-in/sign-out without standing up a full host.
/// </summary>
public sealed class AuthControllerTests
{
    private const string AuthScheme = "CachetAuth";

    private sealed class Fixture
    {
        public ILoginService LoginService { get; } = Substitute.For<ILoginService>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
        public IAuthenticationService AuthenticationService { get; } = Substitute.For<IAuthenticationService>();

        public AuthController CreateSut(ClaimsPrincipal? user = null)
        {
            var controller = new AuthController(LoginService, AuditLogService, UserRepository);

            var services = new ServiceCollection();
            services.AddSingleton(AuthenticationService);
            services.AddLogging();
            // ControllerBase.Problem(...) (used on both AuthController.Login failure branches)
            // resolves ProblemDetailsFactory from RequestServices at call time; AddControllers()
            // is the same registration path Program.cs uses, minus MVC's internal factory type
            // being inaccessible from a test assembly.
            services.AddControllers();
            var httpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
            };

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }
    }

    // ---- POST /login -----------------------------------------------------------------------------

    [Fact]
    public async Task Test_Login_Succeeds_SignsInAndReturns200()
    {
        // Arrange
        var fixture = new Fixture();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "8"),
            new(ClaimTypes.Name, "jdoe"),
            new("b2_capability", "Full Access")
        };
        fixture.LoginService
            .LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LoginOutcome
            {
                Succeeded = true,
                UserId = 8,
                DisplayName = "jdoe",
                B2AccessLabel = "Full Access",
                Claims = claims
            });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Login(new LoginRequest { Username = "jdoe", Password = "pw" }, CancellationToken.None);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = okResult.Value.Should().BeOfType<LoginResponse>().Subject;
        body.UserId.Should().Be(8);
        body.DisplayName.Should().Be("jdoe");
        body.B2AccessLabel.Should().Be("Full Access");

        await fixture.AuthenticationService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            AuthScheme,
            Arg.Is<ClaimsPrincipal>(p =>
                p.Claims.Count() == 3 &&
                p.HasClaim(ClaimTypes.NameIdentifier, "8") &&
                p.HasClaim(ClaimTypes.Name, "jdoe") &&
                p.HasClaim("b2_capability", "Full Access")),
            Arg.Any<AuthenticationProperties?>());
    }

    [Fact]
    public async Task Test_Login_InvalidCredentials_Returns401_NeverSignsIn()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.LoginService
            .LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LoginOutcome { Succeeded = false, FailureReason = LoginFailureReason.InvalidCredentials });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Login(new LoginRequest { Username = "jdoe", Password = "wrong" }, CancellationToken.None);

        // Assert
        var problem = result.Should().BeAssignableTo<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await fixture.AuthenticationService.DidNotReceive().SignInAsync(
            Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties?>());
    }

    [Fact]
    public async Task Test_Login_B2AccessUnavailable_Returns401()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.LoginService
            .LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LoginOutcome { Succeeded = false, FailureReason = LoginFailureReason.B2AccessUnavailable });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Login(new LoginRequest { Username = "jdoe", Password = "pw" }, CancellationToken.None);

        // Assert
        var problem = result.Should().BeAssignableTo<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // ---- POST /logout ----------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_LogoutInvalidatesSession()
    {
        // Arrange — this controller-level test proves the audit-before-signout ordering and the
        // SignOutAsync call itself; the full "same cookie now returns 401 on the next request"
        // behavior (the actual session-invalidation effect SC-3 describes) is proven end-to-end in
        // Cachet.ArchiveSync.Api.IntegrationTests.AuthIntegrationTests.Test_SC3_LogoutInvalidatesSession.
        var fixture = new Fixture();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "12") }, "CachetAuth"));
        var sut = fixture.CreateSut(user);

        var callOrder = new List<string>();
        fixture.UserRepository
            .IncrementSessionTokenVersionAsync(12, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1))
            .AndDoes(_ => callOrder.Add("increment"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1))
            .AndDoes(_ => callOrder.Add("audit"));
        fixture.AuthenticationService
            .SignOutAsync(Arg.Any<HttpContext>(), AuthScheme, Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("signout"));

        // Act
        var result = await sut.Logout(CancellationToken.None);

        // Assert — 204; the session_token_version bump (§6.3a) happens first, then the audit row
        // (audit-before-response invariant), then SignOutAsync clears the browser's cookie copy.
        result.Should().BeOfType<NoContentResult>();
        await fixture.UserRepository.Received(1).IncrementSessionTokenVersionAsync(12, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.UserId == 12 && e.Action == AuditAction.Logout && e.TabModule == "Auth"),
            Arg.Any<CancellationToken>());
        await fixture.AuthenticationService.Received(1).SignOutAsync(Arg.Any<HttpContext>(), AuthScheme, Arg.Any<AuthenticationProperties?>());
        callOrder.Should().Equal("increment", "audit", "signout");
    }

    [Fact]
    public async Task Test_Logout_NoUserIdClaim_Returns401_NoAuditWritten()
    {
        // Arrange — anonymous principal with no NameIdentifier claim at all.
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.Logout(CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
        await fixture.UserRepository.DidNotReceive().IncrementSessionTokenVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        await fixture.AuthenticationService.DidNotReceive().SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<AuthenticationProperties?>());
    }
}
