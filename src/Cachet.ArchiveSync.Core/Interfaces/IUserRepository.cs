using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of one <see cref="IUserRepository.InsertAsync"/> call (CAS-020 §5/§6).</summary>
public enum UserInsertOutcome
{
    Created,

    /// <summary><c>UQ_users_username</c> violated — <c>dbo.users_ins</c> does not pre-check-then-insert
    /// (CAS-020 §5), so this is mapped from the native constraint-violation exception, not a
    /// friendly RAISERROR message. <c>409 username-already-exists</c> (§6, §10 SC-7).</summary>
    UsernameAlreadyExists
}

/// <summary>Result of one <see cref="IUserRepository.InsertAsync"/> call.</summary>
public sealed record UserInsertResult
{
    public required UserInsertOutcome Outcome { get; init; }

    public int? UserId { get; init; }
}

/// <summary>
/// Read access to <c>dbo.users</c> (CAS-003 §4) for the login flow (CAS-002 §6.1 step 1) and,
/// per CAS-002 §6.3a, read/write access to the <c>session_token_version</c> column used for
/// server-side logout revocation. CAS-020 §3/§5 adds the first write paths to this table besides
/// the session-token-version counter — create/edit/deactivate/reactivate, backing the admin
/// provisioning API.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Looks up a user by <paramref name="username"/> (case-sensitivity per <c>UQ_users_username</c>
    /// collation — CAS-003 §4). Returns <c>null</c> if no row matches; the caller (CAS-002 §6.1
    /// step 1/§4) is responsible for returning the same generic invalid-credentials failure for
    /// "unknown username" as for "known username, wrong password" — this method itself makes no
    /// such distinction.
    /// </summary>
    Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct);

    /// <summary>
    /// Looks up a user by <paramref name="userId"/> (CAS-018 §2 step 1 — Permanent Delete's live
    /// per-user B2 authorization check re-fetches the acting user's stored, encrypted B2 key by
    /// id, since nothing outside the login flow could previously do so). Returns <c>null</c> if no
    /// matching row exists; the caller is responsible for failing closed in that case (CAS-018 §2
    /// step 3 — abort the whole request, do not touch any file).
    /// </summary>
    Task<UserAccount?> GetByIdAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Current <c>dbo.users.session_token_version</c> for <paramref name="userId"/> (CAS-002
    /// §6.3a) — a cheap indexed point lookup called from <c>OnValidatePrincipal</c> on every
    /// authenticated request. Returns <c>null</c> if no matching user row exists (e.g. the user
    /// was deleted after the session was issued); the caller must fail closed (reject the
    /// principal) in that case.
    /// </summary>
    Task<int?> GetSessionTokenVersionAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Increments <c>dbo.users.session_token_version</c> by 1 for <paramref name="userId"/>
    /// (CAS-002 §6.3/§6.3a) — called from <c>POST /logout</c> so every cookie issued before this
    /// call is rejected on its next use, regardless of its own sliding-expiration window. Returns
    /// the new version value.
    /// </summary>
    Task<int> IncrementSessionTokenVersionAsync(int userId, CancellationToken ct);

    /// <summary>
    /// CAS-020 §5/§6 — calls <c>dbo.users_ins</c>. <paramref name="passwordHash"/> and both B2
    /// ciphertext values must already be hashed/encrypted by the caller (§12) — this method never
    /// sees plaintext. <paramref name="createdBy"/>/<paramref name="updatedBy"/> are the fixed
    /// constant <c>"admin-provisioning"</c> (§5, §15 resolved). Maps the native
    /// <c>UQ_users_username</c> constraint-violation exception to
    /// <see cref="UserInsertOutcome.UsernameAlreadyExists"/> rather than pre-checking (§5).
    /// </summary>
    Task<UserInsertResult> InsertAsync(
        string createdBy,
        string updatedBy,
        string username,
        string passwordHash,
        string b2ApplicationKeyIdCiphertext,
        string b2ApplicationKeyCiphertext,
        CancellationToken ct);

    /// <summary>
    /// CAS-020 §5/§6 — calls <c>dbo.users_upd</c>, a partial update by <paramref name="userId"/>.
    /// Any <c>null</c> parameter leaves that column unchanged (NULL-means-"leave unchanged", per
    /// the procedure's COALESCE semantics — §5). Returns rows-affected so the caller can map an
    /// unknown <paramref name="userId"/> to <c>404 user-not-found</c> (§6).
    /// </summary>
    Task<int> UpdateAsync(
        int userId,
        string updatedBy,
        string? passwordHash,
        string? b2ApplicationKeyIdCiphertext,
        string? b2ApplicationKeyCiphertext,
        CancellationToken ct);

    /// <summary>
    /// CAS-020 §5/§6 — calls <c>dbo.users_set_active</c>, shared by deactivate
    /// (<paramref name="isActive"/> = <c>false</c>) and reactivate (<paramref name="isActive"/> =
    /// <c>true</c>). Returns rows-affected so the caller can map an unknown
    /// <paramref name="userId"/> to <c>404 user-not-found</c> (§6). Idempotent — setting an
    /// already-matching value still returns a positive rows-affected count (§5).
    /// </summary>
    Task<int> SetActiveAsync(int userId, string updatedBy, bool isActive, CancellationToken ct);

    /// <summary>
    /// CAS-026 §5/§6.4 — calls <c>dbo.users_mark_task_manager_viewed</c>. Sets
    /// <c>dbo.users.task_manager_last_viewed_at</c> to now for <paramref name="userId"/> only (§7
    /// V8 — <paramref name="userId"/> is always the authenticated caller's own claim, never
    /// client-supplied, so this can never touch another user's row). Single-purpose rather than
    /// routing through <see cref="UpdateAsync"/>, matching that method's own precedent
    /// (<see cref="IncrementSessionTokenVersionAsync"/>) of narrow, purpose-specific update
    /// procedures for single-column changes.
    /// </summary>
    Task MarkTaskManagerViewedAsync(int userId, string updatedBy, CancellationToken ct);
}
