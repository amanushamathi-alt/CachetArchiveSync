using Cachet.ArchiveSync.Api.Filters;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="AdminProvisioningTokenFilter"/> (CAS-020 §3/§12) — the action filter
/// gating all four admin-provisioning endpoints on the <c>X-Admin-Provisioning-Key</c> header,
/// checked against the <c>Admin:ProvisioningKey</c> config value, before the action method (and
/// therefore any database write) runs. Proves SC-3 at the filter level directly (no HTTP pipeline
/// needed to prove "rejected before the action runs" — <see cref="ActionExecutionDelegate"/> is
/// never invoked).
/// </summary>
public sealed class AdminProvisioningTokenFilterTests
{
    private const string HeaderName = "X-Admin-Provisioning-Key";
    private const string ConfiguredKey = "correct-admin-key-value";

    private static (AdminProvisioningTokenFilter Sut, ActionExecutingContext Context, ActionExecutionDelegate Next, IConfiguration Configuration) CreateSut(
        string? suppliedHeaderValue, string? configuredValue = ConfiguredKey)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Admin:ProvisioningKey"].Returns(configuredValue);
        var logger = Substitute.For<ILogger<AdminProvisioningTokenFilter>>();

        var httpContext = new DefaultHttpContext();
        if (suppliedHeaderValue is not null)
        {
            httpContext.Request.Headers[HeaderName] = suppliedHeaderValue;
        }

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        var context = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());

        var next = Substitute.For<ActionExecutionDelegate>();
        next().Returns(Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object())));

        return (new AdminProvisioningTokenFilter(configuration, logger), context, next, configuration);
    }

    // ---- SC-3: missing header short-circuits with 401, next() (and therefore the action/any DB
    // write) is never invoked --------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_MissingAdminToken_Returns401NoDbWrite()
    {
        // Arrange
        var (sut, context, next, _) = CreateSut(suppliedHeaderValue: null);

        // Act
        await sut.OnActionExecutionAsync(context, next);

        // Assert — 401, and next() (the action delegate — the only path that could reach the
        // service/repository and therefore the database) is never called.
        context.Result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)context.Result!).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await next.DidNotReceive().Invoke();
    }

    [Fact]
    public async Task Test_WrongAdminToken_Returns401NoDbWrite()
    {
        // Arrange
        var (sut, context, next, _) = CreateSut(suppliedHeaderValue: "not-the-right-key");

        // Act
        await sut.OnActionExecutionAsync(context, next);

        // Assert
        context.Result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)context.Result!).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await next.DidNotReceive().Invoke();
    }

    [Fact]
    public async Task Test_ValidAdminToken_CallsNext()
    {
        // Arrange
        var (sut, context, next, _) = CreateSut(suppliedHeaderValue: ConfiguredKey);

        // Act
        await sut.OnActionExecutionAsync(context, next);

        // Assert — the action is allowed to run; no 401 result was set on the context.
        context.Result.Should().BeNull();
        await next.Received(1).Invoke();
    }

    [Fact]
    public async Task Test_MissingConfiguredAdminKey_ThrowsInvalidOperationException()
    {
        // Arrange — defensive re-check: Program.cs is the primary fail-fast path, but this filter
        // must not silently accept requests if the config value is somehow unset at request time.
        var (sut, context, next, _) = CreateSut(suppliedHeaderValue: ConfiguredKey, configuredValue: null);

        // Act
        var act = () => sut.OnActionExecutionAsync(context, next);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        await next.DidNotReceive().Invoke();
    }
}
