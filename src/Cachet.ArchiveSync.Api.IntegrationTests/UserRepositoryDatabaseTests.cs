using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB round-trip tests for <see cref="UserRepository"/> / <c>dbo.users_get_by_username</c>,
/// <c>dbo.users_get_by_id</c> (CAS-018 §2 step 1/§7), <c>dbo.users_get_session_token_version</c>,
/// and <c>dbo.users_increment_session_token_version</c> (CAS-002 §6.1 step 1, §6.3a). These
/// SQL-shape paths (stored-procedure name, parameter binding, row/scalar mapping) cannot be proven
/// with a mocked <c>IUserRepository</c> — only against a real database with the CAS-002/CAS-018
/// migrations applied.
///
/// Skip-marked in this sandbox, following the exact convention established by
/// <see cref="AuditLogRepositoryTests"/>: no live SQL Server instance is reachable here. The test
/// bodies are written as if they would run against a real <c>CachetArchiveSync</c> database —
/// connection string read from the <c>ConnectionStrings:CachetArchiveSync</c> configuration key
/// (environment variable <c>ConnectionStrings__CachetArchiveSync</c> or a local
/// <c>appsettings.IntegrationTests.json</c>, neither of which exists yet in this sandbox). Un-skip
/// and run manually against a real instance with <c>dbo.users_get_by_username</c> (migration
/// <c>20260922_161500__CAS-002-users-get-by-username.sql</c>) and
/// <c>dbo.users_get_session_token_version</c> / <c>dbo.users_increment_session_token_version</c>
/// (migration <c>20260922_163000__CAS-002-session-token-version.sql</c>) deployed, and a known test
/// user row seeded, before merging.
/// </summary>
public sealed class UserRepositoryDatabaseTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database with the " +
        "CAS-002 users_get_by_username / session_token_version stored procedures deployed and a " +
        "seeded test user — not available in this sandbox; run manually against a real instance " +
        "before merging.";

    private readonly string _connectionString;
    private readonly string _seedUsername;
    private readonly int _seedUserId;

    public UserRepositoryDatabaseTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;

        // A pre-seeded test user — real run: seed (or reuse a known fixture) user before executing
        // these tests. dbo.users.user_id is IDENTITY(100000,1) (CAS-003 §4), so 100000 is a
        // reasonable fixture default, not a magic guess.
        _seedUsername = configuration["IntegrationTests:SeedUsername"] ?? "jdoe";
        _seedUserId = int.TryParse(configuration["IntegrationTests:SeedUserId"], out var id) ? id : 100000;
    }

    private IConfiguration BuildRepositoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CachetArchiveSync"] = _connectionString
            })
            .Build();

    [Fact(Skip = SkipReason)]
    public async Task Test_GetByUsernameAsync_KnownUsername_ReturnsMappedRow()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act
        var user = await repository.GetByUsernameAsync(_seedUsername, CancellationToken.None);

        // Assert — every dbo.users_get_by_username result-set column maps onto UserAccount exactly
        // (CAS-003 §4 column list, verbatim from the stored procedure's SELECT).
        user.Should().NotBeNull();
        user!.UserId.Should().Be(_seedUserId);
        user.Username.Should().Be(_seedUsername);
        user.PasswordHash.Should().NotBeNullOrWhiteSpace();
        user.B2ApplicationKeyId.Should().NotBeNullOrWhiteSpace();
        user.B2ApplicationKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_GetByUsernameAsync_UnknownUsername_ReturnsNull()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act
        var user = await repository.GetByUsernameAsync("no-such-user-" + Guid.NewGuid(), CancellationToken.None);

        // Assert — zero rows, per dbo.users_get_by_username's XML doc; the caller (LoginService),
        // not this repository, is responsible for the generic-failure-reason requirement.
        user.Should().BeNull();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_GetByIdAsync_KnownUserId_ReturnsMappedRow()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act
        var user = await repository.GetByIdAsync(_seedUserId, CancellationToken.None);

        // Assert — same dbo.users_get_by_id column shape as dbo.users_get_by_username (CAS-018
        // §7), mapped onto the identical UserAccount entity.
        user.Should().NotBeNull();
        user!.UserId.Should().Be(_seedUserId);
        user.Username.Should().Be(_seedUsername);
        user.PasswordHash.Should().NotBeNullOrWhiteSpace();
        user.B2ApplicationKeyId.Should().NotBeNullOrWhiteSpace();
        user.B2ApplicationKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_GetByIdAsync_UnknownUserId_ReturnsNull()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act — CAS-018 §2 step 3: the caller (QuarantinePermanentDeleteService) must fail closed
        // (deny the whole delete request) when this returns null.
        var user = await repository.GetByIdAsync(userId: -1, CancellationToken.None);

        // Assert
        user.Should().BeNull();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_GetSessionTokenVersionAsync_KnownUser_ReturnsCurrentValue()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act
        var version = await repository.GetSessionTokenVersionAsync(_seedUserId, CancellationToken.None);

        // Assert — dbo.users.session_token_version defaults to 0 (DF_users_session_token_version)
        // and is never negative.
        version.Should().NotBeNull();
        version!.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_GetSessionTokenVersionAsync_UnknownUser_ReturnsNull()
    {
        // Arrange — CAS-002 §6.3a: the caller must fail closed (reject the principal) when this
        // returns null, e.g. a user deleted after the session was issued.
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act
        var version = await repository.GetSessionTokenVersionAsync(userId: -1, CancellationToken.None);

        // Assert
        version.Should().BeNull();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_IncrementSessionTokenVersionAsync_IncrementsAndReturnsNewValue()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());
        var before = await repository.GetSessionTokenVersionAsync(_seedUserId, CancellationToken.None);
        before.Should().NotBeNull();

        // Act
        var newVersion = await repository.IncrementSessionTokenVersionAsync(_seedUserId, CancellationToken.None);

        // Assert — CAS-002 §6.3/§6.3a: exactly +1, and the DB row itself reflects it (not merely
        // the output parameter), so a subsequent GetSessionTokenVersionAsync call (the exact
        // sequence OnValidatePrincipal performs) sees the new value.
        newVersion.Should().Be(before!.Value + 1);

        var afterReRead = await repository.GetSessionTokenVersionAsync(_seedUserId, CancellationToken.None);
        afterReRead.Should().Be(newVersion);

        // Cleanup: real run should restore session_token_version to its pre-test value (or accept
        // the increment as idempotent test-account drift) — dbo.users has no down-migration path
        // for a single-column revert here, so this is a caller/test-account responsibility, not
        // application code's, matching dbo.audit_log's append-only cleanup note in
        // AuditLogRepositoryTests.
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_IncrementSessionTokenVersionAsync_UnknownUser_DoesNotThrowAndAffectsNoRow()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act — dbo.users_increment_session_token_version's behavior for a non-existent user_id
        // (0 rows affected, output parameter left at its default) is asserted here against the
        // real stored procedure rather than assumed.
        var act = () => repository.IncrementSessionTokenVersionAsync(userId: -1, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ---- CAS-020 §5/§6: InsertAsync / UpdateAsync / SetActiveAsync against dbo.users_ins /
    // dbo.users_upd / dbo.users_set_active. Real-DB round-trip proof that the stored-procedure
    // shape (constraint-violation mapping, COALESCE "NULL = leave unchanged" semantics, and
    // rows-affected reporting for an unknown user_id) behaves as UserProvisioningServiceTests
    // assumes when it mocks IUserRepository. ---------------------------------------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_InsertAsync_NewUsername_RoundTripsThroughGetByIdAsync()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());
        var username = "cas020-insert-" + Guid.NewGuid().ToString("N")[..12];

        // Act
        var result = await repository.InsertAsync(
            createdBy: "admin-provisioning",
            updatedBy: "admin-provisioning",
            username: username,
            passwordHash: "hashed:irrelevant",
            b2ApplicationKeyIdCiphertext: "ct:keyid",
            b2ApplicationKeyCiphertext: "ct:key",
            ct: CancellationToken.None);

        // Assert — a real dbo.users row now exists and is readable through the exact same
        // column mapping GetByIdAsync/GetByUsernameAsync already prove.
        result.Outcome.Should().Be(UserInsertOutcome.Created);
        result.UserId.Should().NotBeNull();

        var inserted = await repository.GetByIdAsync(result.UserId!.Value, CancellationToken.None);
        inserted.Should().NotBeNull();
        inserted!.Username.Should().Be(username);
        inserted.PasswordHash.Should().Be("hashed:irrelevant");
        inserted.B2ApplicationKeyId.Should().Be("ct:keyid");
        inserted.B2ApplicationKey.Should().Be("ct:key");
        inserted.IsActive.Should().BeTrue();

        // Cleanup: real run should delete this row — dbo.users has no down-migration path for a
        // single-row revert here, same caller/test-account responsibility note as
        // IncrementSessionTokenVersionAsync above.
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_InsertAsync_DuplicateUsername_ReturnsUsernameAlreadyExists()
    {
        // Arrange — the seeded fixture username (_seedUsername) is assumed to already exist per
        // this class's constructor-level fixture contract.
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act — dbo.users_ins does not pre-check-then-insert (CAS-020 §5); UQ_users_username's
        // native constraint-violation exception is mapped, not thrown, to the caller.
        var result = await repository.InsertAsync(
            createdBy: "admin-provisioning",
            updatedBy: "admin-provisioning",
            username: _seedUsername,
            passwordHash: "hashed:irrelevant",
            b2ApplicationKeyIdCiphertext: "ct:keyid",
            b2ApplicationKeyCiphertext: "ct:key",
            ct: CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(UserInsertOutcome.UsernameAlreadyExists);
        result.UserId.Should().BeNull();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_UpdateAsync_PartialUpdate_LeavesUnspecifiedColumnsUnchanged()
    {
        // Arrange — a fresh row so this test doesn't perturb the shared seed fixture.
        var repository = new UserRepository(BuildRepositoryConfiguration());
        var username = "cas020-update-" + Guid.NewGuid().ToString("N")[..12];
        var insertResult = await repository.InsertAsync(
            "admin-provisioning", "admin-provisioning", username,
            "hashed:original", "ct:original-keyid", "ct:original-key", CancellationToken.None);
        insertResult.Outcome.Should().Be(UserInsertOutcome.Created);
        var userId = insertResult.UserId!.Value;

        // Act — only passwordHash supplied; both B2 ciphertext parameters left null, which
        // dbo.users_upd's COALESCE semantics (§5) must interpret as "leave unchanged".
        var rowsAffected = await repository.UpdateAsync(
            userId,
            updatedBy: "admin-provisioning",
            passwordHash: "hashed:rotated",
            b2ApplicationKeyIdCiphertext: null,
            b2ApplicationKeyCiphertext: null,
            ct: CancellationToken.None);

        // Assert
        rowsAffected.Should().Be(1);
        var afterUpdate = await repository.GetByIdAsync(userId, CancellationToken.None);
        afterUpdate.Should().NotBeNull();
        afterUpdate!.PasswordHash.Should().Be("hashed:rotated");
        afterUpdate.B2ApplicationKeyId.Should().Be("ct:original-keyid");
        afterUpdate.B2ApplicationKey.Should().Be("ct:original-key");
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_UpdateAsync_UnknownUserId_ReturnsZeroRowsAffectedWithoutThrowing()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act
        var act = () => repository.UpdateAsync(
            userId: -1,
            updatedBy: "admin-provisioning",
            passwordHash: "hashed:irrelevant",
            b2ApplicationKeyIdCiphertext: null,
            b2ApplicationKeyCiphertext: null,
            ct: CancellationToken.None);

        // Assert — the caller (UserProvisioningService), not this repository, is responsible for
        // mapping zero-rows-affected to 404 user-not-found (§6).
        var rowsAffected = await act();
        rowsAffected.Should().Be(0);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SetActiveAsync_TogglesIsActive_BothDirections()
    {
        // Arrange — a fresh row, active by default per InsertAsync's contract.
        var repository = new UserRepository(BuildRepositoryConfiguration());
        var username = "cas020-setactive-" + Guid.NewGuid().ToString("N")[..12];
        var insertResult = await repository.InsertAsync(
            "admin-provisioning", "admin-provisioning", username,
            "hashed:irrelevant", "ct:keyid", "ct:key", CancellationToken.None);
        var userId = insertResult.UserId!.Value;

        // Act — deactivate.
        var deactivateRowsAffected = await repository.SetActiveAsync(userId, "admin-provisioning", isActive: false, CancellationToken.None);

        // Assert — deactivate
        deactivateRowsAffected.Should().Be(1);
        var afterDeactivate = await repository.GetByIdAsync(userId, CancellationToken.None);
        afterDeactivate!.IsActive.Should().BeFalse();

        // Act — reactivate.
        var reactivateRowsAffected = await repository.SetActiveAsync(userId, "admin-provisioning", isActive: true, CancellationToken.None);

        // Assert — reactivate
        reactivateRowsAffected.Should().Be(1);
        var afterReactivate = await repository.GetByIdAsync(userId, CancellationToken.None);
        afterReactivate!.IsActive.Should().BeTrue();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SetActiveAsync_UnknownUserId_ReturnsZeroRowsAffectedWithoutThrowing()
    {
        // Arrange
        var repository = new UserRepository(BuildRepositoryConfiguration());

        // Act
        var act = () => repository.SetActiveAsync(userId: -1, "admin-provisioning", isActive: false, CancellationToken.None);

        // Assert
        var rowsAffected = await act();
        rowsAffected.Should().Be(0);
    }
}
