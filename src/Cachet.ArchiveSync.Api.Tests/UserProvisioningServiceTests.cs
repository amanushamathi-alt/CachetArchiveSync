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
/// Unit tests for <see cref="UserProvisioningService"/> (CAS-020 §3/§6/§7/§12) — the service layer
/// backing all four admin-provisioning endpoints. Proves SC-1, SC-2, V1-V6, and the branch coverage
/// the karpathy-coding-principles skill requires for every outcome the service can return
/// (validation failure, not-found, B2-key-invalid, username-conflict, and the happy path for each
/// of create/edit/deactivate/reactivate). SC-4/SC-5/SC-6/SC-7/SC-8/SC-9/SC-3 are proven at the
/// HTTP layer in <c>UsersAdminControllerTests</c>/<c>AdminProvisioningTokenFilterTests</c> per
/// CAS-020 §13's own test-plan class assignment.
/// </summary>
public sealed class UserProvisioningServiceTests
{
    private const string PlaintextKeyId = "0043xxxxxxxxxxxxxxxxxxxx";
    private const string PlaintextKey = "K0043xxxxxxxxxxxxxxxxxxxxxxxxxxxx";
    private const string CiphertextKeyId = "ct:keyid-new";
    private const string CiphertextKey = "ct:key-new";

    private static readonly PasswordHasher<object> Hasher = new();

    private sealed class Fixture
    {
        public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
        public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
        public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<UserProvisioningService> Logger { get; } = Substitute.For<ILogger<UserProvisioningService>>();

        public Fixture()
        {
            // Default: B2 authorization succeeds; Protect returns distinguishable ciphertext.
            B2KeyAuthorizer
                .AuthorizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new B2AuthorizationInfo
                {
                    AccountId = "acct-1",
                    AuthorizationToken = "4_secretB2token_neverLog",
                    ApiUrl = "https://api-fake.backblazeb2.com",
                    DownloadUrl = "https://download-fake.backblazeb2.com",
                    Capabilities = new[] { "listFiles", "readFiles", "writeFiles" },
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
                });

            CredentialProtector.Protect(PlaintextKeyId).Returns(CiphertextKeyId);
            CredentialProtector.Protect(PlaintextKey).Returns(CiphertextKey);
        }

        public UserProvisioningService CreateSut() =>
            new(UserRepository, B2KeyAuthorizer, CredentialProtector, AuditLogService, Logger);
    }

    private static CreateUserRequest ValidCreateRequest() => new()
    {
        Username = "m.alvarez",
        Password = "a-strong-password",
        B2ApplicationKeyId = PlaintextKeyId,
        B2ApplicationKey = PlaintextKey
    };

    // ---- SC-1: created row persists a hashed password and encrypted (never plaintext) B2 columns ---

    [Fact]
    public async Task Test_SC1_CreateUser_PersistsHashedAndEncryptedCredentials()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository
            .InsertAsync("admin-provisioning", "admin-provisioning", "m.alvarez", Arg.Any<string>(), CiphertextKeyId, CiphertextKey, Arg.Any<CancellationToken>())
            .Returns(new UserInsertResult { Outcome = UserInsertOutcome.Created, UserId = 100005 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.CreateAsync(ValidCreateRequest(), CancellationToken.None);

        // Assert — outcome succeeded, and the repository call the service actually made carries a
        // password hash (never the plaintext password) and the mocked ciphertext (never plaintext
        // B2 values). PasswordHasher.VerifyHashedPassword proves the stored value is a real,
        // verifiable hash, not merely "not equal to the raw string".
        result.Outcome.Should().Be(UserCreateOutcome.Created);

        var call = fixture.UserRepository.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IUserRepository.InsertAsync));
        var args = call.GetArguments();
        var persistedPasswordHash = (string)args[3]!;
        var persistedKeyIdCiphertext = (string)args[4]!;
        var persistedKeyCiphertext = (string)args[5]!;

        persistedPasswordHash.Should().NotBe("a-strong-password");
        Hasher.VerifyHashedPassword(new object(), persistedPasswordHash, "a-strong-password")
            .Should().Be(PasswordVerificationResult.Success);

        persistedKeyIdCiphertext.Should().Be(CiphertextKeyId).And.NotBe(PlaintextKeyId);
        persistedKeyCiphertext.Should().Be(CiphertextKey).And.NotBe(PlaintextKey);
    }

    // ---- SC-2: response body never echoes password/B2 key/key-id -----------------------------------

    [Fact]
    public async Task Test_SC2_CreateUser_ResponseNeverEchoesSecrets()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UserInsertResult { Outcome = UserInsertOutcome.Created, UserId = 100005 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.CreateAsync(ValidCreateRequest(), CancellationToken.None);

        // Assert — CreateUserResponse only ever has UserId/Username/CreatedAt fields (proven by the
        // type itself, §6), and none of the field values equal any secret input.
        result.Response.Should().NotBeNull();
        result.Response!.UserId.Should().Be(100005);
        result.Response.Username.Should().Be("m.alvarez");
        result.Response.Username.Should().NotBe("a-strong-password").And.NotBe(PlaintextKeyId).And.NotBe(PlaintextKey);
    }

    // ---- V1: username missing/empty/too long -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Test_V1_CreateUser_MissingUsername_Returns400(string? username)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidCreateRequest() with { Username = username };

        // Act
        var result = await sut.CreateAsync(request, CancellationToken.None);

        // Assert — validation fails before any repository/B2 call.
        result.Outcome.Should().Be(UserCreateOutcome.ValidationFailed);
        await fixture.UserRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.B2KeyAuthorizer.DidNotReceive().AuthorizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V1_CreateUser_UsernameTooLong_Returns400()
    {
        // Arrange — 51 chars, over the NVARCHAR(50) limit.
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidCreateRequest() with { Username = new string('a', 51) };

        // Act
        var result = await sut.CreateAsync(request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserCreateOutcome.ValidationFailed);
    }

    // ---- V2: password required, minimum 8 characters -----------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short1")]
    public async Task Test_V2_CreateUser_PasswordTooShort_Returns400(string? password)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidCreateRequest() with { Password = password };

        // Act
        var result = await sut.CreateAsync(request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserCreateOutcome.ValidationFailed);
        await fixture.UserRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- V3: b2ApplicationKeyId/b2ApplicationKey required together on create -----------------------

    [Theory]
    [InlineData(null, "K0043keyvalue")]
    [InlineData("0043keyid", null)]
    [InlineData(null, null)]
    public async Task Test_V3_CreateUser_MissingB2Key_Returns400(string? keyId, string? key)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = ValidCreateRequest() with { B2ApplicationKeyId = keyId, B2ApplicationKey = key };

        // Act
        var result = await sut.CreateAsync(request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserCreateOutcome.ValidationFailed);
        await fixture.B2KeyAuthorizer.DidNotReceive().AuthorizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- V4: edit with an empty body is rejected ----------------------------------------------------

    [Fact]
    public async Task Test_V4_EditUser_EmptyBody_Returns400()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.EditAsync(100005, new EditUserRequest(), CancellationToken.None);

        // Assert — rejected before any GetByIdAsync/UpdateAsync call.
        result.Outcome.Should().Be(UserEditOutcome.ValidationFailed);
        await fixture.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.UserRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- V5: edit's B2 key pair must arrive together; password (if supplied) meets V2's minimum ----

    [Theory]
    [InlineData("0043newkeyid", null)]
    [InlineData(null, "K0043newkeyvalue")]
    public async Task Test_V5_EditUser_PartialB2Key_Returns400(string? keyId, string? key)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new EditUserRequest { B2ApplicationKeyId = keyId, B2ApplicationKey = key };

        // Act
        var result = await sut.EditAsync(100005, request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserEditOutcome.ValidationFailed);
        await fixture.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V5_EditUser_PasswordTooShort_Returns400()
    {
        // Arrange — password supplied but under V2's 8-character minimum.
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new EditUserRequest { Password = "short1" };

        // Act
        var result = await sut.EditAsync(100005, request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserEditOutcome.ValidationFailed);
    }

    // ---- V6 / SC-9: a supplied B2 key must pass live authorization before persistence --------------

    [Fact]
    public async Task Test_V6_CreateUser_InvalidB2Key_Returns422()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.B2KeyAuthorizer
            .AuthorizeAsync(PlaintextKeyId, PlaintextKey, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "b2_authorize_account failed for the supplied user key."));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.CreateAsync(ValidCreateRequest(), CancellationToken.None);

        // Assert — 422, and no partial write: InsertAsync never called (SC-9).
        result.Outcome.Should().Be(UserCreateOutcome.B2KeyInvalid);
        await fixture.UserRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC9_EditUser_InvalidB2Key_NoPartialUpdate()
    {
        // Arrange — existing user found (so the 404-before-422 ordering doesn't short-circuit),
        // but the live B2 check on the new key fails.
        var fixture = new Fixture();
        fixture.UserRepository.GetByIdAsync(100005, Arg.Any<CancellationToken>())
            .Returns(new UserAccount { UserId = 100005, Username = "m.alvarez", PasswordHash = "irrelevant", B2ApplicationKeyId = "ct:old", B2ApplicationKey = "ct:old", IsActive = true, SessionTokenVersion = 0 });
        fixture.B2KeyAuthorizer
            .AuthorizeAsync(PlaintextKeyId, PlaintextKey, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "revoked"));
        var sut = fixture.CreateSut();
        var request = new EditUserRequest { B2ApplicationKeyId = PlaintextKeyId, B2ApplicationKey = PlaintextKey };

        // Act
        var result = await sut.EditAsync(100005, request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserEditOutcome.B2KeyInvalid);
        await fixture.UserRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-7: duplicate username on create maps to 409 --------------------------------------------

    [Fact]
    public async Task Test_SC7_DuplicateUsername_Returns409()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UserInsertResult { Outcome = UserInsertOutcome.UsernameAlreadyExists });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.CreateAsync(ValidCreateRequest(), CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserCreateOutcome.UsernameAlreadyExists);
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- Edit: 404 is checked BEFORE the 422 live-B2-authorization call (implementer note) ----------

    [Fact]
    public async Task Test_EditUser_UnknownUserId_Returns404_NeverCallsB2Authorizer()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository.GetByIdAsync(999999, Arg.Any<CancellationToken>()).Returns((UserAccount?)null);
        var sut = fixture.CreateSut();
        var request = new EditUserRequest { B2ApplicationKeyId = PlaintextKeyId, B2ApplicationKey = PlaintextKey };

        // Act
        var result = await sut.EditAsync(999999, request, CancellationToken.None);

        // Assert — 404, and the live B2 call (an external network call) never happens for a
        // nonexistent user (ordering the implementer called out explicitly).
        result.Outcome.Should().Be(UserEditOutcome.UserNotFound);
        await fixture.B2KeyAuthorizer.DidNotReceive().AuthorizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.UserRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_EditUser_PasswordOnly_UpdatesWithoutTouchingB2Key()
    {
        // Arrange — partial update: only password supplied, B2 key fields left null/unchanged.
        var fixture = new Fixture();
        fixture.UserRepository.GetByIdAsync(100005, Arg.Any<CancellationToken>())
            .Returns(new UserAccount { UserId = 100005, Username = "m.alvarez", PasswordHash = "irrelevant", B2ApplicationKeyId = "ct:old", B2ApplicationKey = "ct:old", IsActive = true, SessionTokenVersion = 0 });
        fixture.UserRepository.UpdateAsync(
                100005, "admin-provisioning", Arg.Any<string>(), Arg.Is<string?>(x => x == null), Arg.Is<string?>(x => x == null), Arg.Any<CancellationToken>())
            .Returns(1);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.EditAsync(100005, new EditUserRequest { Password = "a-new-strong-password" }, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserEditOutcome.Updated);
        await fixture.B2KeyAuthorizer.DidNotReceive().AuthorizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.UserUpdated && e.UserId == 100005 && e.TabModule == "Auth" && e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }

    // ---- Deactivate / Reactivate: not-found and happy-path branches --------------------------------

    [Fact]
    public async Task Test_DeactivateAsync_UnknownUserId_Returns404_NoAuditRow()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository.SetActiveAsync(999999, "admin-provisioning", false, Arg.Any<CancellationToken>()).Returns(0);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.DeactivateAsync(999999, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserSetActiveOutcome.UserNotFound);
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_DeactivateAsync_ExistingUser_SetsInactiveAndWritesAudit()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository.SetActiveAsync(100005, "admin-provisioning", false, Arg.Any<CancellationToken>()).Returns(1);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.DeactivateAsync(100005, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserSetActiveOutcome.Updated);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.UserDeactivated && e.UserId == 100005 && e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_ReactivateAsync_ExistingUser_SetsActiveAndWritesAudit()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository.SetActiveAsync(100005, "admin-provisioning", true, Arg.Any<CancellationToken>()).Returns(1);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ReactivateAsync(100005, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserSetActiveOutcome.Updated);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.UserReactivated && e.UserId == 100005 && e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_ReactivateAsync_UnknownUserId_Returns404()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository.SetActiveAsync(999999, "admin-provisioning", true, Arg.Any<CancellationToken>()).Returns(0);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ReactivateAsync(999999, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserSetActiveOutcome.UserNotFound);
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-6 (unit-level slice): audit write happens, carrying the fixed admin identity -----------

    [Fact]
    public async Task Test_SC6_CreateUser_WritesAuditRowWithFixedAdminIdentity()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.UserRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UserInsertResult { Outcome = UserInsertOutcome.Created, UserId = 100005 });
        var sut = fixture.CreateSut();

        // Act
        await sut.CreateAsync(ValidCreateRequest(), CancellationToken.None);

        // Assert — one audit row, fixed "admin-provisioning" identity on both created_by/updated_by
        // (§12, §15 resolved), whatever TabModule value this service actually uses ("Auth" —
        // confirmed by reading UserProvisioningService.cs; no new "Admin" tab_module value exists).
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.CreatedBy == "admin-provisioning" &&
                e.UpdatedBy == "admin-provisioning" &&
                e.TabModule == "Auth" &&
                e.Action == AuditAction.UserCreated &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }
}
