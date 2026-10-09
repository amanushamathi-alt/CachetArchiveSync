using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IUserRepository"/>. Calls
/// <c>dbo.users_get_by_username</c> (CAS-002 §5, authored by db-author,
/// migration 20260922_161500__CAS-002-users-get-by-username.sql) and, for §6.3a session
/// revocation, <c>dbo.users_get_session_token_version</c> / <c>dbo.users_increment_session_token_
/// version</c> (migration 20260922_163000__CAS-002-session-token-version.sql).
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

    public async Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@username", username);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "dbo.users_get_by_username",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        return row is null
            ? null
            : new UserAccount
            {
                UserId = row.user_id,
                Username = row.username,
                PasswordHash = row.password_hash,
                B2ApplicationKeyId = row.b2_application_key_id,
                B2ApplicationKey = row.b2_application_key,
                IsActive = row.is_active,
                SessionTokenVersion = row.session_token_version
            };
    }

    /// <summary>CAS-018 §2 step 1 — calls <c>dbo.users_get_by_id</c> (migration
    /// 20260923_150000__CAS-018-users-get-by-id.sql).</summary>
    public async Task<UserAccount?> GetByIdAsync(int userId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@user_id", userId);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "dbo.users_get_by_id",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        return row is null
            ? null
            : new UserAccount
            {
                UserId = row.user_id,
                Username = row.username,
                PasswordHash = row.password_hash,
                B2ApplicationKeyId = row.b2_application_key_id,
                B2ApplicationKey = row.b2_application_key,
                IsActive = row.is_active,
                SessionTokenVersion = row.session_token_version
            };
    }

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

    /// <summary>CAS-002 §6.3/§6.3a — calls <c>dbo.users_increment_session_token_version</c>.</summary>
    public async Task<int> IncrementSessionTokenVersionAsync(int userId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@user_id", userId);
        parameters.Add(
            "@new_session_token_version",
            dbType: System.Data.DbType.Int32,
            direction: System.Data.ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.users_increment_session_token_version",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@new_session_token_version");
    }

    // UQ_users_username's native SQL Server unique-constraint-violation error number.
    private const int UniqueConstraintViolationNumber = 2627;
    private const string UsernameUniqueConstraintName = "UQ_users_username";

    /// <summary>CAS-020 §5/§6 — calls <c>dbo.users_ins</c>.</summary>
    public async Task<UserInsertResult> InsertAsync(
        string createdBy,
        string updatedBy,
        string username,
        string passwordHash,
        string b2ApplicationKeyIdCiphertext,
        string b2ApplicationKeyCiphertext,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@username", username);
        parameters.Add("@password_hash", passwordHash);
        parameters.Add("@b2_application_key_id", b2ApplicationKeyIdCiphertext);
        parameters.Add("@b2_application_key", b2ApplicationKeyCiphertext);
        parameters.Add("@user_id", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "dbo.users_ins",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (
            ex.Number == UniqueConstraintViolationNumber
            && ex.Message.Contains(UsernameUniqueConstraintName, StringComparison.Ordinal))
        {
            return new UserInsertResult { Outcome = UserInsertOutcome.UsernameAlreadyExists };
        }

        return new UserInsertResult
        {
            Outcome = UserInsertOutcome.Created,
            UserId = parameters.Get<int>("@user_id")
        };
    }

    /// <summary>CAS-020 §5/§6 — calls <c>dbo.users_upd</c>. NULL parameters leave that column
    /// unchanged (COALESCE semantics owned by the procedure — §5).</summary>
    public async Task<int> UpdateAsync(
        int userId,
        string updatedBy,
        string? passwordHash,
        string? b2ApplicationKeyIdCiphertext,
        string? b2ApplicationKeyCiphertext,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@user_id", userId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@password_hash", passwordHash);
        parameters.Add("@b2_application_key_id", b2ApplicationKeyIdCiphertext);
        parameters.Add("@b2_application_key", b2ApplicationKeyCiphertext);
        parameters.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.users_upd",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@rows_affected");
    }

    /// <summary>CAS-020 §5/§6 — calls <c>dbo.users_set_active</c>, shared by deactivate/reactivate.</summary>
    public async Task<int> SetActiveAsync(int userId, string updatedBy, bool isActive, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@user_id", userId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@is_active", isActive);
        parameters.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.users_set_active",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@rows_affected");
    }

    /// <summary>CAS-026 §5/§6.4 — calls <c>dbo.users_mark_task_manager_viewed</c>.</summary>
    public async Task MarkTaskManagerViewedAsync(int userId, string updatedBy, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@user_id", userId);
        parameters.Add("@updated_by", updatedBy);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.users_mark_task_manager_viewed",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    // Dapper's default mapping is case-insensitive against snake_case column names; a private
    // record keeps the SQL-shaped names out of the public UserAccount entity (Core).
    private sealed record UserRow(
        int user_id,
        string username,
        string password_hash,
        string b2_application_key_id,
        string b2_application_key,
        bool is_active,
        int session_token_version);
}
