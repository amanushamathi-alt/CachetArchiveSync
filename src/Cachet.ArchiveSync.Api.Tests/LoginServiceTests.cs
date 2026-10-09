using System.Security.Claims;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="LoginService"/> (CAS-002 §6.1). Proves SC-1, SC-8 (partial — see
/// <c>AuthIntegrationTests</c> for the full-HTTP-round-trip half), and SC-6 at reduced scope (no
/// <c>GET /tasks</c> endpoint or task-queue repository exists yet in this codebase — see this
/// file's <see cref="Test_SC6_TaskListScopedToOwnUser"/> XML doc for what is actually proven here).
/// Also covers every branch CAS-002 §6.1 defines: unknown username, wrong password, inactive
/// account, decrypt failure, B2 authorization failure, and both capability-label derivations.
/// </summary>
public sealed class LoginServiceTests
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private const string CiphertextKeyId = "ct:keyid";
    private const string CiphertextKey = "ct:key";
    private const string PlaintextKeyId = "0000000000000000000000001";
    private const string PlaintextKey = "K000RawApplicationKeyValueNeverLeak";

    private static readonly PasswordHasher<object> Hasher = new();

    private static UserAccount ValidUser(int userId = 1, string username = ValidUsername, bool isActive = true, int sessionTokenVersion = 0) =>
        new()
        {
            UserId = userId,
            Username = username,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = CiphertextKeyId,
            B2ApplicationKey = CiphertextKey,
            IsActive = isActive,
            SessionTokenVersion = sessionTokenVersion
        };

    private sealed class Fixture
    {
        public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
        public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
        public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<LoginService> Logger { get; } = Substitute.For<ILogger<LoginService>>();

        public Fixture()
        {
            CredentialProtector.Unprotect(CiphertextKeyId).Returns(PlaintextKeyId);
            CredentialProtector.Unprotect(CiphertextKey).Returns(PlaintextKey);
        }

        public LoginService CreateSut() =>
            new(UserRepository, B2KeyAuthorizer, CredentialProtector, AuditLogService, Logger);

        public void SetB2Capabilities(params string[] capabilities) =>
            B2KeyAuthorizer
                .AuthorizeAsync(PlaintextKeyId, PlaintextKey, Arg.Any<CancellationToken>())
                .Returns(new B2AuthorizationInfo
                {
                    AccountId = "acct-1",
                    AuthorizationToken = "4_secretB2token_neverLog",
                    ApiUrl = "https://api-fake.backblazeb2.com",
                    DownloadUrl = "https://download-fake.backblazeb2.com",
                    Capabilities = capabilities,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
                });
    }

    private static List<string> GetFormattedLogMessages(ILogger<LoginService> logger)
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

    // ---- SC-1: successful login issues exactly the four safe claims (§6.1 step 4, incl. §6.3a's
    // session_ver revocation claim) ------------------------

    [Fact]
    public async Task Test_SC1_LoginClaimsExcludeSecrets()
    {
        // Arrange
        var fixture = new Fixture();
        var user = ValidUser(userId: 7, sessionTokenVersion: 3);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        fixture.SetB2Capabilities("listFiles", "readFiles", "writeFiles");
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = ValidUsername, Password = ValidPassword }, CancellationToken.None);

        // Assert — exactly four claims, the four §6.1 step 4 names, and none of them (by type or
        // value) is credential-shaped.
        outcome.Succeeded.Should().BeTrue();
        outcome.Claims.Should().HaveCount(4);
        outcome.Claims!.Select(c => c.Type).Should().BeEquivalentTo(
            new[] { ClaimTypes.NameIdentifier, ClaimTypes.Name, "b2_capability", "session_ver" });

        outcome.Claims!.Single(c => c.Type == ClaimTypes.NameIdentifier).Value.Should().Be("7");
        outcome.Claims!.Single(c => c.Type == ClaimTypes.Name).Value.Should().Be(ValidUsername);
        outcome.Claims!.Single(c => c.Type == "b2_capability").Value.Should().Be("Full Access");
        outcome.Claims!.Single(c => c.Type == "session_ver").Value.Should().Be("3");

        outcome.Claims!.Should().OnlyContain(c =>
            c.Value != PlaintextKeyId &&
            c.Value != PlaintextKey &&
            c.Value != user.PasswordHash &&
            c.Value != CiphertextKeyId &&
            c.Value != CiphertextKey);
    }

    // ---- SC-8 (partial): no secret ever reaches a claim, the outcome DTO, or a log line --------

    [Fact]
    public async Task Test_SC8_NoCredentialLeakageAcrossAuthSurface()
    {
        var fixture = new Fixture();

        // --- Success path: outcome carries no secret-shaped value anywhere -----------------------
        var user = ValidUser(userId: 3);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        fixture.SetB2Capabilities("listFiles", "readFiles");
        var sut = fixture.CreateSut();

        var successOutcome = await sut.LoginAsync(
            new LoginRequest { Username = ValidUsername, Password = ValidPassword }, CancellationToken.None);

        successOutcome.Succeeded.Should().BeTrue();
        successOutcome.DisplayName.Should().NotContain(PlaintextKey);
        successOutcome.B2AccessLabel.Should().NotBe(PlaintextKey);

        // --- Failure path: a wrong-password attempt logs a warning that never contains the
        // supplied (or stored) secret material.
        var failureOutcome = await sut.LoginAsync(
            new LoginRequest { Username = ValidUsername, Password = "wrong-password-entirely" }, CancellationToken.None);

        failureOutcome.Succeeded.Should().BeFalse();

        var loggedMessages = GetFormattedLogMessages(fixture.Logger);
        loggedMessages.Should().NotBeEmpty();
        loggedMessages.Should().OnlyContain(m =>
            !m.Contains(ValidPassword) &&
            !m.Contains(PlaintextKey) &&
            !m.Contains(PlaintextKeyId) &&
            !m.Contains(user.PasswordHash));
    }

    // ---- SC-6 (reduced scope — see XML doc) -----------------------------------------------------

    /// <summary>
    /// SC-6 ("<c>GET /tasks</c> called by user A never returns a task row whose
    /// <c>submitted_by_user_id</c> belongs to user B") cannot be proven directly: this codebase has
    /// no <c>GET /tasks</c> endpoint or task-queue repository yet (both belong to a not-yet-built
    /// spec). Per the orchestrator's explicit scope-reality guidance, this test instead proves the
    /// one piece of CAS-002's own session design that any future <c>GET /tasks</c> implementation
    /// would depend on for that scoping: that two different users' sessions never share, cross-
    /// leak, or fall back to a common/default <c>NameIdentifier</c> (user_id) claim — the value a
    /// task-ownership filter would key off of. This is a session-claims-level proxy for SC-6, not a
    /// full proof of the eventual endpoint's own filtering logic.
    /// </summary>
    [Fact]
    public async Task Test_SC6_TaskListScopedToOwnUser()
    {
        // Arrange — two distinct users, each with their own credentials.
        var fixture = new Fixture();
        var userA = ValidUser(userId: 101, username: "userA");
        var userB = ValidUser(userId: 202, username: "userB");
        fixture.UserRepository.GetByUsernameAsync("userA", Arg.Any<CancellationToken>()).Returns(userA);
        fixture.UserRepository.GetByUsernameAsync("userB", Arg.Any<CancellationToken>()).Returns(userB);
        fixture.SetB2Capabilities("listFiles", "readFiles");
        var sut = fixture.CreateSut();

        // Act
        var outcomeA = await sut.LoginAsync(new LoginRequest { Username = "userA", Password = ValidPassword }, CancellationToken.None);
        var outcomeB = await sut.LoginAsync(new LoginRequest { Username = "userB", Password = ValidPassword }, CancellationToken.None);

        // Assert — each session's NameIdentifier claim is that user's own id, and only that id.
        outcomeA.Claims!.Single(c => c.Type == ClaimTypes.NameIdentifier).Value.Should().Be("101");
        outcomeB.Claims!.Single(c => c.Type == ClaimTypes.NameIdentifier).Value.Should().Be("202");
        outcomeA.Claims!.Should().OnlyContain(c => c.Type != ClaimTypes.NameIdentifier || c.Value != "202");
        outcomeB.Claims!.Should().OnlyContain(c => c.Type != ClaimTypes.NameIdentifier || c.Value != "101");
    }

    // ---- Branch coverage: §6.1 step 1 failures ---------------------------------------------------

    [Fact]
    public async Task Test_Login_UnknownUsername_ReturnsInvalidCredentials_NoAuditRow()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository.GetByUsernameAsync("nobody", Arg.Any<CancellationToken>()).Returns((UserAccount?)null);
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = "nobody", Password = "irrelevant" }, CancellationToken.None);

        // Assert — generic InvalidCredentials, and no audit row (no valid user_id to attach it to;
        // dbo.audit_log.user_id is NOT NULL with an FK to dbo.users, per the implementer's own
        // BLOCKED note in LoginService.cs).
        outcome.Succeeded.Should().BeFalse();
        outcome.FailureReason.Should().Be(LoginFailureReason.InvalidCredentials);
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_Login_WrongPassword_ReturnsInvalidCredentials_WritesLoginFailedAudit()
    {
        // Arrange
        var fixture = new Fixture();
        var user = ValidUser(userId: 9);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = ValidUsername, Password = "not-the-password" }, CancellationToken.None);

        // Assert — same generic failure reason as "unknown username" (must not distinguish), and an
        // audit row IS written this time since a valid user_id exists.
        outcome.Succeeded.Should().BeFalse();
        outcome.FailureReason.Should().Be(LoginFailureReason.InvalidCredentials);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.UserId == 9 && e.Action == AuditAction.LoginFailed && e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_Login_InactiveUser_ReturnsInvalidCredentials()
    {
        // Arrange
        var fixture = new Fixture();
        var user = ValidUser(userId: 11, isActive: false);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = ValidUsername, Password = ValidPassword }, CancellationToken.None);

        // Assert
        outcome.Succeeded.Should().BeFalse();
        outcome.FailureReason.Should().Be(LoginFailureReason.InvalidCredentials);
    }

    // ---- Branch coverage: §6.1 step 2 failures ---------------------------------------------------

    [Fact]
    public async Task Test_Login_DecryptFailure_ReturnsB2AccessUnavailable_WritesAudit()
    {
        // Arrange
        var fixture = new Fixture();
        var user = ValidUser(userId: 13);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        fixture.CredentialProtector.Unprotect(CiphertextKeyId).Throws(new InvalidOperationException("key ring mismatch"));
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = ValidUsername, Password = ValidPassword }, CancellationToken.None);

        // Assert
        outcome.Succeeded.Should().BeFalse();
        outcome.FailureReason.Should().Be(LoginFailureReason.B2AccessUnavailable);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.UserId == 13 && e.Action == AuditAction.LoginFailed && e.ResultDetail == "B2 Access Unavailable"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_Login_B2AuthorizeThrows_ReturnsB2AccessUnavailable_WritesAudit()
    {
        // Arrange
        var fixture = new Fixture();
        var user = ValidUser(userId: 17);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        fixture.B2KeyAuthorizer
            .AuthorizeAsync(PlaintextKeyId, PlaintextKey, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "b2_authorize_account failed for the supplied user key."));
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = ValidUsername, Password = ValidPassword }, CancellationToken.None);

        // Assert
        outcome.Succeeded.Should().BeFalse();
        outcome.FailureReason.Should().Be(LoginFailureReason.B2AccessUnavailable);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.UserId == 17 && e.Action == AuditAction.LoginFailed),
            Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: §6.1 step 3 capability derivation ---------------------------------------

    [Theory]
    [InlineData(new[] { "listFiles", "readFiles", "writeFiles" }, "Full Access")]
    [InlineData(new[] { "listFiles", "readFiles", "deleteFiles" }, "Full Access")]
    [InlineData(new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" }, "Full Access")]
    [InlineData(new[] { "listFiles", "readFiles" }, "Read Only")]
    [InlineData(new string[0], "Read Only")]
    public async Task Test_Login_CapabilityDerivation_MapsToExpectedLabel(string[] capabilities, string expectedLabel)
    {
        // Arrange
        var fixture = new Fixture();
        var user = ValidUser(userId: 21);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        fixture.SetB2Capabilities(capabilities);
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = ValidUsername, Password = ValidPassword }, CancellationToken.None);

        // Assert
        outcome.Succeeded.Should().BeTrue();
        outcome.B2AccessLabel.Should().Be(expectedLabel);
        outcome.Claims!.Single(c => c.Type == "b2_capability").Value.Should().Be(expectedLabel);
    }

    [Fact]
    public async Task Test_Login_Success_WritesLoginAuditRow()
    {
        // Arrange
        var fixture = new Fixture();
        var user = ValidUser(userId: 55);
        fixture.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(user);
        fixture.SetB2Capabilities("listFiles", "readFiles");
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.LoginAsync(new LoginRequest { Username = ValidUsername, Password = ValidPassword }, CancellationToken.None);

        // Assert
        outcome.Succeeded.Should().BeTrue();
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.UserId == 55 &&
                e.TabModule == "Auth" &&
                e.Action == AuditAction.Login &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }
}
