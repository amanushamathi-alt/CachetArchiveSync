using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Web.Data;

/// <summary>
/// Web-side <see cref="IUserRepository"/> implementation, used only by the CAS-002 §6.3a
/// <c>OnValidatePrincipal</c> session-revocation check registered in this project's
/// <c>Program.cs</c>. Web hosts the authentication cookie (§6.6) and must be able to re-check
/// <c>dbo.users.session_token_version</c> on every request in its own process, independent of
/// Api, per §6.3a's reasoning ("Api and Web are separate processes that must agree on revocation
/// state without a shared-memory mechanism, and <c>dbo.users</c> is already the single source of
/// truth both processes read"). This is the one narrow exception to this project's general
/// "Web calls Api over HTTP, never touches Core's data-access implementations directly"
/// convention (clean-architecture skill) — the spec is explicit here (CAS-002 §6.3a: "registered
/// identically in both Api's and Web's Program.cs", "IUserRepository resolved via
/// RequestServices"), so the spec's instruction takes precedence (CLAUDE.md skill-precedence
/// table: spec above local conventions).
///
/// Web never performs login or logout itself — those flows are Api's (§6.1/§6.3), reached from
/// Web only via the forwarded-cookie HttpClient (§6.6) — so <see cref="GetByUsernameAsync"/> and
/// <see cref="IncrementSessionTokenVersionAsync"/> are intentionally unsupported here rather than
/// duplicating write/credential-lookup logic Web has no other reason to hold.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly string _connectionString;

    public UserRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct) =>
        throw new NotSupportedException(
            "Web never performs the login lookup itself (CAS-002 §6.1 is Api-only); this " +
            "repository exists solely for the §6.3a session-revocation check.");

    public Task<UserAccount?> GetByIdAsync(int userId, CancellationToken ct) =>
        throw new NotSupportedException(
            "Web never performs Permanent Delete's live per-user B2 authorization check itself " +
            "(CAS-018 §2 is Api-only); this repository exists solely for the §6.3a " +
            "session-revocation check.");

    /// <summary>CAS-002 §6.3a — calls <c>dbo.users_get_session_token_version</c>.</summary>
    public async Task<int?> GetSessionTokenVersionAsync(int userId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@user_id", userId);

        await using var connection = new SqlConnection(_connectionString);

        return await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "dbo.users_get_session_token_version",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    public Task<int> IncrementSessionTokenVersionAsync(int userId, CancellationToken ct) =>
        throw new NotSupportedException(
            "Web never performs logout's server-side increment itself (CAS-002 §6.3 is " +
            "Api-only, reached via the forwarded-cookie HttpClient, §6.6); this repository " +
            "exists solely for the §6.3a session-revocation check.");

    // CAS-020 (touches.ui: false) added InsertAsync/UpdateAsync/SetActiveAsync to the shared
    // IUserRepository interface for the Api-only admin provisioning surface. Stubbed here, not
    // implemented, for the same reason as the other unsupported members above — Web has no
    // provisioning UI (CAS-020 §4/§8, explicit non-goal) and must not talk to dbo.users beyond the
    // §6.3a session-revocation check.
    public Task<UserInsertResult> InsertAsync(
        string createdBy,
        string updatedBy,
        string username,
        string passwordHash,
        string b2ApplicationKeyIdCiphertext,
        string b2ApplicationKeyCiphertext,
        CancellationToken ct) =>
        throw new NotSupportedException(
            "Web never performs admin user provisioning itself (CAS-020 is Api-only, no UI — " +
            "§4/§8); this repository exists solely for the §6.3a session-revocation check.");

    public Task<int> UpdateAsync(
        int userId,
        string updatedBy,
        string? passwordHash,
        string? b2ApplicationKeyIdCiphertext,
        string? b2ApplicationKeyCiphertext,
        CancellationToken ct) =>
        throw new NotSupportedException(
            "Web never performs admin user provisioning itself (CAS-020 is Api-only, no UI — " +
            "§4/§8); this repository exists solely for the §6.3a session-revocation check.");

    public Task<int> SetActiveAsync(int userId, string updatedBy, bool isActive, CancellationToken ct) =>
        throw new NotSupportedException(
            "Web never performs admin user provisioning itself (CAS-020 is Api-only, no UI — " +
            "§4/§8); this repository exists solely for the §6.3a session-revocation check.");

    // CAS-026 (touches.ui: false) added MarkTaskManagerViewedAsync to the shared IUserRepository
    // interface for the Api-only POST /tasks/mark-viewed endpoint. Stubbed here, not implemented,
    // for the same reason as the other unsupported members above — the Task Manager screen (once
    // built) reaches this action over HTTP via Api, the same as every other Web-to-Api call; Web
    // itself has no reason to touch dbo.users beyond the §6.3a session-revocation check.
    public Task MarkTaskManagerViewedAsync(int userId, string updatedBy, CancellationToken ct) =>
        throw new NotSupportedException(
            "Web never performs the Task Manager mark-viewed action itself (CAS-026 is Api-only, " +
            "reached over HTTP once CAS-027 builds the Task Manager screen); this repository " +
            "exists solely for the §6.3a session-revocation check.");
}
