using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.UsersAdmin;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-020 §6's four admin-provisioning endpoints, run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via
/// <see cref="UsersAdminApiWebApplicationFactory"/>. Proves the spec's §13 Test Plan
/// "UsersAdminControllerTests" assignment: SC-4, SC-5, SC-6, SC-7, SC-8, SC-9, plus verb/route/401
/// coverage for every endpoint (SC-3 is proven directly at the filter level in
/// <c>AdminProvisioningTokenFilterTests</c>; this class adds the HTTP-level corroboration).
/// </summary>
public sealed class UsersAdminControllerTests : IAsyncLifetime
{
    private const string ValidPassword = "correct horse battery staple";
    private static readonly PasswordHasher<object> Hasher = new();

    private UsersAdminApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new UsersAdminApiWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string url, object? body = null, bool includeAdminHeader = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (includeAdminHeader)
        {
            request.Headers.TryAddWithoutValidation(
                UsersAdminApiWebApplicationFactory.AdminHeaderName, UsersAdminApiWebApplicationFactory.AdminProvisioningKey);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    private void SeedB2Authorization(string keyId, string key, params string[] capabilities) =>
        _factory.B2KeyAuthorizer
            .AuthorizeAsync(keyId, key, Arg.Any<CancellationToken>())
            .Returns(new B2AuthorizationInfo
            {
                AccountId = "acct-1",
                AuthorizationToken = "4_secretB2token_neverLog",
                ApiUrl = "https://api-fake.backblazeb2.com",
                DownloadUrl = "https://download-fake.backblazeb2.com",
                Capabilities = capabilities,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });

    private async Task<string> LoginAsync(string username, string password = ValidPassword)
    {
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/B2 fakes are configured to succeed");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(UsersAdminApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
        cookie.Should().NotBeNull();
        return cookie!.Split(';')[0];
    }

    // ---- SC-7: duplicate username on create returns 409 --------------------------------------------

    [Fact]
    public async Task Test_SC7_DuplicateUsername_Returns409()
    {
        // Arrange
        SeedB2Authorization("0043dupid", "K0043dupkey", "listFiles", "readFiles");
        _factory.UserRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "existing.user", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UserInsertResult { Outcome = UserInsertOutcome.UsernameAlreadyExists });

        // Act
        using var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/admin/users", new CreateUserRequest
        {
            Username = "existing.user",
            Password = "a-strong-password",
            B2ApplicationKeyId = "0043dupid",
            B2ApplicationKey = "K0043dupkey"
        }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _factory.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-9: an invalid B2 key/key ID pair is rejected with 422, no partial write -----------------

    [Fact]
    public async Task Test_SC9_InvalidB2Key_RejectsWithNoPartialWrite()
    {
        // Arrange — the live authorization call itself fails (bad key), for both create and edit.
        _factory.B2KeyAuthorizer
            .AuthorizeAsync("0043badid", "K0043badkey", Arg.Any<CancellationToken>())
            .Throws(new Cachet.ArchiveSync.Core.Exceptions.B2ApiException(401, "bad_auth_token", "invalid"));

        // Act — create
        using var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/admin/users", new CreateUserRequest
        {
            Username = "newuser.badkey",
            Password = "a-strong-password",
            B2ApplicationKeyId = "0043badid",
            B2ApplicationKey = "K0043badkey"
        }));

        // Assert — create
        createResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await _factory.UserRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Arrange — edit, existing user found
        _factory.UserRepository.GetByIdAsync(500001, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = 500001,
            Username = "existing.user2",
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:old-id",
            B2ApplicationKey = "ct:old-key",
            IsActive = true,
            SessionTokenVersion = 0
        });

        // Act — edit
        using var editResponse = await _client.SendAsync(AdminRequest(HttpMethod.Put, "/admin/users/500001", new EditUserRequest
        {
            B2ApplicationKeyId = "0043badid",
            B2ApplicationKey = "K0043badkey"
        }));

        // Assert — edit: 422, and no partial update ever reached the repository.
        editResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await _factory.UserRepository.DidNotReceive().UpdateAsync(
            500001, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _factory.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-6: every operation writes exactly one audit row before the response returns -------------

    [Fact]
    public async Task Test_SC6_EachOperation_WritesAuditLogBeforeResponse()
    {
        // --- Create -------------------------------------------------------------------------------
        SeedB2Authorization("0043createid", "K0043createkey", "listFiles", "readFiles");
        _factory.UserRepository
            .InsertAsync("admin-provisioning", "admin-provisioning", "sc6.user", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UserInsertResult { Outcome = UserInsertOutcome.Created, UserId = 600001 });

        using (var createResponse = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/admin/users", new CreateUserRequest
        {
            Username = "sc6.user",
            Password = "a-strong-password",
            B2ApplicationKeyId = "0043createid",
            B2ApplicationKey = "K0043createkey"
        })))
        {
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.UserCreated && e.UserId == 600001 &&
                e.CreatedBy == "admin-provisioning" && e.UpdatedBy == "admin-provisioning"),
            Arg.Any<CancellationToken>());

        // --- Edit ---------------------------------------------------------------------------------
        _factory.UserRepository.GetByIdAsync(600001, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = 600001,
            Username = "sc6.user",
            PasswordHash = "irrelevant",
            B2ApplicationKeyId = "ct:x",
            B2ApplicationKey = "ct:x",
            IsActive = true,
            SessionTokenVersion = 0
        });
        _factory.UserRepository.UpdateAsync(
                600001, "admin-provisioning", Arg.Any<string>(), Arg.Is<string?>(x => x == null), Arg.Is<string?>(x => x == null), Arg.Any<CancellationToken>())
            .Returns(1);

        using (var editResponse = await _client.SendAsync(AdminRequest(HttpMethod.Put, "/admin/users/600001", new EditUserRequest
        {
            Password = "a-new-strong-password"
        })))
        {
            editResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.UserUpdated && e.UserId == 600001 &&
                e.CreatedBy == "admin-provisioning" && e.UpdatedBy == "admin-provisioning"),
            Arg.Any<CancellationToken>());

        // --- Deactivate -----------------------------------------------------------------------------
        _factory.UserRepository.SetActiveAsync(600001, "admin-provisioning", false, Arg.Any<CancellationToken>()).Returns(1);

        using (var deactivateResponse = await _client.SendAsync(AdminRequest(HttpMethod.Patch, "/admin/users/600001/deactivate")))
        {
            deactivateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.UserDeactivated && e.UserId == 600001 &&
                e.CreatedBy == "admin-provisioning" && e.UpdatedBy == "admin-provisioning"),
            Arg.Any<CancellationToken>());

        // --- Reactivate -----------------------------------------------------------------------------
        _factory.UserRepository.SetActiveAsync(600001, "admin-provisioning", true, Arg.Any<CancellationToken>()).Returns(1);

        using (var reactivateResponse = await _client.SendAsync(AdminRequest(HttpMethod.Patch, "/admin/users/600001/reactivate")))
        {
            reactivateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.UserReactivated && e.UserId == 600001 &&
                e.CreatedBy == "admin-provisioning" && e.UpdatedBy == "admin-provisioning"),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-4: PUT with a new B2 key persists it encrypted with the same purpose string that a
    // subsequent real login decrypts with (a genuine round trip through the real
    // DataProtectionCredentialProtector — see factory XML doc) -----------------------------------

    [Fact]
    public async Task Test_SC4_EditUser_NewB2Key_DecryptsCorrectlyAtLogin()
    {
        // Arrange — an existing user, currently keyed with some old (now-irrelevant) ciphertext.
        const int userId = 700001;
        const string username = "sc4.user";
        var sessionTokenVersion = _factory.RegisterSessionTokenVersion(userId);
        var existingUser = new UserAccount
        {
            UserId = userId,
            Username = username,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:old-id-irrelevant",
            B2ApplicationKey = "ct:old-key-irrelevant",
            IsActive = true,
            SessionTokenVersion = sessionTokenVersion
        };
        _factory.UserRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(existingUser);
        _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(callInfo => existingUser);

        const string newKeyId = "0043newkeyid000000000000";
        const string newKey = "K0043newkeyvalueeeeeeeeeeeeeeeeeee";
        SeedB2Authorization(newKeyId, newKey, "listFiles", "readFiles", "writeFiles");

        // Capture whatever ciphertext the real DataProtectionCredentialProtector produces for the
        // new key, and feed it back out of GetByIdAsync/GetByUsernameAsync so the subsequent login
        // decrypts exactly what was persisted (UpdateAsync itself is a mocked repository call, so
        // this test wires the captured ciphertext back into the read path it would have landed in).
        string? capturedKeyIdCiphertext = null;
        string? capturedKeyCiphertext = null;
        _factory.UserRepository
            .When(r => r.UpdateAsync(userId, "admin-provisioning", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                capturedKeyIdCiphertext = callInfo.ArgAt<string?>(3);
                capturedKeyCiphertext = callInfo.ArgAt<string?>(4);
            });
        _factory.UserRepository.UpdateAsync(userId, "admin-provisioning", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(1);

        // Act — edit with the new B2 key.
        using var editResponse = await _client.SendAsync(AdminRequest(HttpMethod.Put, $"/admin/users/{userId}", new EditUserRequest
        {
            B2ApplicationKeyId = newKeyId,
            B2ApplicationKey = newKey
        }));

        // Assert — edit succeeded and persisted ciphertext distinct from the plaintext.
        editResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        capturedKeyIdCiphertext.Should().NotBeNull().And.NotBe(newKeyId);
        capturedKeyCiphertext.Should().NotBeNull().And.NotBe(newKey);

        // Now re-point the read path at the newly "persisted" ciphertext, exactly as a real
        // dbo.users row would reflect after the update, and log in — the real
        // DataProtectionCredentialProtector must decrypt back to the exact new plaintext key, which
        // the seeded B2KeyAuthorizer only accepts for (newKeyId, newKey).
        var updatedUser = existingUser with { B2ApplicationKeyId = capturedKeyIdCiphertext!, B2ApplicationKey = capturedKeyCiphertext! };
        _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(updatedUser);

        var cookie = await LoginAsync(username);
        cookie.Should().NotBeNullOrEmpty();
    }

    // ---- SC-5: deactivate blocks a subsequent login via the existing InvalidCredentials path -------

    [Fact]
    public async Task Test_SC5_Deactivate_BlocksSubsequentLogin()
    {
        // Arrange
        const int userId = 800001;
        const string username = "sc5.user";
        var sessionTokenVersion = _factory.RegisterSessionTokenVersion(userId);
        var userAccount = new UserAccount
        {
            UserId = userId,
            Username = username,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = sessionTokenVersion
        };
        _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(userAccount);
        _factory.UserRepository.SetActiveAsync(userId, "admin-provisioning", false, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                userAccount = userAccount with { IsActive = false };
                _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(userAccount);
                return 1;
            });

        // Act — deactivate.
        using var deactivateResponse = await _client.SendAsync(AdminRequest(HttpMethod.Patch, $"/admin/users/{userId}/deactivate"));
        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Act — login now fails with the existing generic InvalidCredentials path (never a new
        // failure mode).
        using var loginResponse = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });

        // Assert
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-8: reactivate restores login ability ----------------------------------------------------

    [Fact]
    public async Task Test_SC8_Reactivate_RestoresLoginAbility()
    {
        // Arrange — a previously-deactivated user. The B2 key columns must hold ciphertext the
        // REAL DataProtectionCredentialProtector registered in this factory (see its XML doc) can
        // actually decrypt once IsActive flips true and login reaches that step — so this test
        // resolves the app's own ICredentialProtector instance and calls its real Protect(...),
        // exactly as UserProvisioningService itself would have done at original provisioning time.
        const int userId = 900001;
        const string username = "sc8.user";
        const string plainKeyId = "0043sc8keyid00000000000000";
        const string plainKey = "K0043sc8keyvalueeeeeeeeeeeeeeeeeee";

        var credentialProtector = _factory.Services.GetRequiredService<ICredentialProtector>();
        var keyIdCiphertext = credentialProtector.Protect(plainKeyId);
        var keyCiphertext = credentialProtector.Protect(plainKey);

        var sessionTokenVersion = _factory.RegisterSessionTokenVersion(userId);
        var userAccount = new UserAccount
        {
            UserId = userId,
            Username = username,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = keyIdCiphertext,
            B2ApplicationKey = keyCiphertext,
            IsActive = false,
            SessionTokenVersion = sessionTokenVersion
        };
        _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(userAccount);
        _factory.UserRepository.SetActiveAsync(userId, "admin-provisioning", true, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                userAccount = userAccount with { IsActive = true };
                _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(userAccount);
                return 1;
            });
        SeedB2Authorization(plainKeyId, plainKey, "listFiles", "readFiles");

        // Precondition — login fails while still inactive.
        using (var preLoginResponse = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword }))
        {
            preLoginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Act — reactivate.
        using var reactivateResponse = await _client.SendAsync(AdminRequest(HttpMethod.Patch, $"/admin/users/{userId}/reactivate"));
        reactivateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — login now succeeds again.
        using var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Verb/route/auth coverage: every endpoint requires the admin header -------------------------

    [Theory]
    [InlineData("POST", "/admin/users")]
    [InlineData("PUT", "/admin/users/1")]
    [InlineData("PATCH", "/admin/users/1/deactivate")]
    [InlineData("PATCH", "/admin/users/1/reactivate")]
    public async Task Test_AllEndpoints_MissingAdminToken_Returns401(string method, string url)
    {
        // Act
        using var response = await _client.SendAsync(AdminRequest(new HttpMethod(method), url, includeAdminHeader: false));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Test_EditUser_UnknownUserId_Returns404()
    {
        // Arrange
        _factory.UserRepository.GetByIdAsync(999999, Arg.Any<CancellationToken>()).Returns((UserAccount?)null);

        // Act
        using var response = await _client.SendAsync(AdminRequest(HttpMethod.Put, "/admin/users/999999", new EditUserRequest { Password = "a-new-strong-password" }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_Deactivate_UnknownUserId_Returns404()
    {
        // Arrange
        _factory.UserRepository.SetActiveAsync(999999, "admin-provisioning", false, Arg.Any<CancellationToken>()).Returns(0);

        // Act
        using var response = await _client.SendAsync(AdminRequest(HttpMethod.Patch, "/admin/users/999999/deactivate"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Reactivate: unknown userId maps to 404, mirroring Deactivate ------------------------------

    [Fact]
    public async Task Test_Reactivate_UnknownUserId_Returns404()
    {
        // Arrange
        _factory.UserRepository.SetActiveAsync(999999, "admin-provisioning", true, Arg.Any<CancellationToken>()).Returns(0);

        // Act
        using var response = await _client.SendAsync(AdminRequest(HttpMethod.Patch, "/admin/users/999999/reactivate"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await _factory.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- Controller-level 400 branches: proves UsersAdminController.Create/Edit's own
    // ValidationFailed switch arm maps to 400 over HTTP, not just at the service-unit level
    // (UserProvisioningServiceTests already proves the service returns ValidationFailed itself). ---

    [Fact]
    public async Task Test_CreateUser_EmptyBody_Returns400()
    {
        // Act — an empty request body: Username/Password/B2 fields all missing, so the service's
        // §7 V1/V2/V3 checks fail before any repository or B2 call.
        using var response = await _client.SendAsync(AdminRequest(HttpMethod.Post, "/admin/users", new CreateUserRequest()));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _factory.UserRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _factory.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_EditUser_EmptyBody_Returns400()
    {
        // Act — §7 V4: an edit request with no fields supplied at all is rejected before the
        // controller ever consults GetByIdAsync.
        using var response = await _client.SendAsync(AdminRequest(HttpMethod.Put, "/admin/users/1", new EditUserRequest()));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _factory.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }
}
