using Cachet.ArchiveSync.Web.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Cachet.ArchiveSync.Web.Tests;

/// <summary>
/// Unit tests for <see cref="CookieForwardingHandler"/> (CAS-002 §6.6). Proves SC-7: a server-side
/// call from Web to Api forwards the current Blazor Server circuit's session cookie unchanged, so
/// Api's identical cookie-authentication scheme recognizes it as the same session — no second
/// login. A full two-process round trip (Web calling a real Api instance) is out of reach for a
/// unit test; this proves the one thing <see cref="CookieForwardingHandler"/> itself is
/// responsible for — that the outgoing request carries the exact incoming <c>Cookie</c> header —
/// which is the necessary condition for Api-side recognition (Api's own cookie validation is
/// proven independently in <c>Cachet.ArchiveSync.Api.IntegrationTests.AuthIntegrationTests</c>).
/// </summary>
public sealed class CookieForwardingHandlerTests
{
    /// <summary>Captures the last request that reached it and answers with a fixed 200 OK.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, CapturingHandler Inner) CreateClient(HttpContext? httpContext)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        var inner = new CapturingHandler();
        var handler = new CookieForwardingHandler(accessor) { InnerHandler = inner };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://cachet.tracecounts.internal/api/") };
        return (client, inner);
    }

    [Fact]
    public async Task Test_SC7_WebToApiSessionForwardingWorks()
    {
        // Arrange — a Blazor Server circuit's current HttpContext carrying the CachetAuth cookie,
        // exactly as ASP.NET Core would populate it for an incoming browser request to Web.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Cookie"] = "CachetAuth=eyJhbGciOi.SIGNED-SESSION-VALUE";
        var (client, inner) = CreateClient(httpContext);

        // Act
        using var response = await client.GetAsync("tasks");

        // Assert — the outgoing call to Api carries the identical Cookie header value; this is the
        // mechanism CAS-002 §6.6 relies on for Api to recognize the same authenticated session
        // without a second login.
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        inner.LastRequest.Should().NotBeNull();
        inner.LastRequest!.Headers.TryGetValues("Cookie", out var cookieValues).Should().BeTrue();
        cookieValues.Should().ContainSingle().Which.Should().Be("CachetAuth=eyJhbGciOi.SIGNED-SESSION-VALUE");
    }

    [Fact]
    public async Task Test_ForwardsMultipleIncomingCookieHeaderValues()
    {
        // Arrange — StringValues can carry more than one raw header value; the handler must forward
        // every one, not just the first, so a non-CachetAuth cookie present alongside it is not
        // silently dropped.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Cookie"] = new Microsoft.Extensions.Primitives.StringValues(
            new[] { "CachetAuth=session-value", "OtherCookie=other-value" });
        var (client, inner) = CreateClient(httpContext);

        // Act
        await client.GetAsync("tasks");

        // Assert
        inner.LastRequest!.Headers.TryGetValues("Cookie", out var cookieValues).Should().BeTrue();
        cookieValues.Should().BeEquivalentTo(new[] { "CachetAuth=session-value", "OtherCookie=other-value" });
    }

    [Fact]
    public async Task Test_NoCurrentHttpContext_ForwardsRequestWithoutCookieHeader()
    {
        // Arrange — e.g. a background/non-circuit call with no current HttpContext. Must not throw.
        var (client, inner) = CreateClient(httpContext: null);

        // Act
        var act = async () => await client.GetAsync("tasks");

        // Assert
        await act.Should().NotThrowAsync();
        inner.LastRequest!.Headers.Contains("Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Test_NoIncomingCookieHeader_ForwardsRequestWithoutCookieHeader()
    {
        // Arrange — an authenticated-looking context but with no Cookie header at all (e.g. a
        // pre-login request on the sign-in page's own circuit).
        var httpContext = new DefaultHttpContext();
        var (client, inner) = CreateClient(httpContext);

        // Act
        await client.GetAsync("tasks");

        // Assert
        inner.LastRequest!.Headers.Contains("Cookie").Should().BeFalse();
    }
}
