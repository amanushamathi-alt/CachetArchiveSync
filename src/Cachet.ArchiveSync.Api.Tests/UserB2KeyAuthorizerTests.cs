using System.Net;
using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Api.B2;
using Cachet.ArchiveSync.Core.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="UserB2KeyAuthorizer"/> (CAS-002 §6.1 step 2 — live
/// <c>b2_authorize_account</c> validation of a caller-supplied user key, deliberately separate
/// from <c>IB2Client</c>; see <c>IUserB2KeyAuthorizer</c>'s XML doc). Uses a fake
/// <see cref="HttpMessageHandler"/> in the same spirit as <c>B2ClientTests.StubAuthorizeHandler</c>
/// (this class's only seam is the <see cref="IHttpClientFactory"/>-created <see cref="HttpClient"/>
/// itself, so a fake transport is the only way to exercise its real request-building and
/// response-mapping logic without a live B2 endpoint).
/// </summary>
public sealed class UserB2KeyAuthorizerTests
{
    private const string KeyId = "0000000000000000000000001";
    private const string ApplicationKey = "K000RawApplicationKeyValueNeverLeak";

    /// <summary>Captures the last request it saw and answers with a pre-configured response.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static (UserB2KeyAuthorizer Sut, FakeHandler Handler, ILogger<UserB2KeyAuthorizer> Logger) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var httpClient = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("B2Client").Returns(httpClient);
        var logger = Substitute.For<ILogger<UserB2KeyAuthorizer>>();

        return (new UserB2KeyAuthorizer(httpClientFactory, logger), handler, logger);
    }

    private static List<string> GetFormattedLogMessages(ILogger<UserB2KeyAuthorizer> logger)
    {
        var messages = new List<string>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    [Fact]
    public async Task Test_AuthorizeAsync_Success_ReturnsCapabilitiesAndUsesBasicAuthWithSuppliedKey()
    {
        // Arrange
        var (sut, handler, _) = CreateSut(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                accountId = "acct-1",
                authorizationToken = "4_secretB2token_neverLog",
                apiUrl = "https://api-fake.backblazeb2.com",
                downloadUrl = "https://download-fake.backblazeb2.com",
                allowed = new { capabilities = new[] { "listFiles", "readFiles" } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        // Act
        var result = await sut.AuthorizeAsync(KeyId, ApplicationKey, CancellationToken.None);

        // Assert — capabilities/fields mapped from the wire response, and the request used HTTP
        // Basic auth built from the CALLER-supplied key pair (CAS-002 §6.1 step 2), never a
        // server-held system key (that invariant belongs to IB2Client/B2Client, per this class's
        // XML doc — this test proves this class's own request never substitutes anything else in).
        result.AccountId.Should().Be("acct-1");
        result.AuthorizationToken.Should().Be("4_secretB2token_neverLog");
        result.Capabilities.Should().BeEquivalentTo(new[] { "listFiles", "readFiles" });

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Basic");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastRequest.Headers.Authorization.Parameter!));
        decoded.Should().Be($"{KeyId}:{ApplicationKey}");
    }

    [Fact]
    public async Task Test_AuthorizeAsync_NoCapabilitiesInResponse_ReturnsEmptyList()
    {
        // Arrange — "allowed" present but capabilities null (defensive branch: payload.Allowed?.Capabilities ?? new List<string>()).
        var (sut, _, _) = CreateSut(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                accountId = "acct-1",
                authorizationToken = "4_secretB2token_neverLog",
                apiUrl = "https://api-fake.backblazeb2.com",
                downloadUrl = "https://download-fake.backblazeb2.com",
                allowed = (object?)null
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        // Act
        var result = await sut.AuthorizeAsync(KeyId, ApplicationKey, CancellationToken.None);

        // Assert
        result.Capabilities.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_AuthorizeAsync_NonSuccessStatus_ThrowsB2ApiException_NeverLeaksKeyOrBody()
    {
        // Arrange — a real bad-key response from B2 (e.g. 401 unauthorized).
        var (sut, _, logger) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { status = 401, code = "bad_auth_token", message = "Invalid authorization token" }),
                Encoding.UTF8,
                "application/json")
        });

        // Act
        var act = () => sut.AuthorizeAsync(KeyId, ApplicationKey, CancellationToken.None);

        // Assert — CAS-002 §6.1 error table: never reveal which check failed or the raw B2 error
        // detail; the thrown exception's message and every logged message must be silent on that,
        // and on the key/application key themselves (§11/§12).
        var exception = await act.Should().ThrowAsync<B2ApiException>();
        exception.Which.StatusCode.Should().Be(401);
        exception.Which.Message.Should().NotContain(KeyId).And.NotContain(ApplicationKey);

        var loggedMessages = GetFormattedLogMessages(logger);
        loggedMessages.Should().NotBeEmpty();
        loggedMessages.Should().OnlyContain(m => !m.Contains(KeyId) && !m.Contains(ApplicationKey));
    }

    [Fact]
    public async Task Test_AuthorizeAsync_EmptySuccessBody_ThrowsB2ApiException()
    {
        // Arrange — a 200 with no parseable body at all (defensive branch: ReadFromJsonAsync
        // returning null throws B2ApiException rather than a NullReferenceException downstream).
        var (sut, _, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });

        // Act
        var act = () => sut.AuthorizeAsync(KeyId, ApplicationKey, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<B2ApiException>();
    }
}
