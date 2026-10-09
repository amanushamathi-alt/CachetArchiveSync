using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of one <see cref="IUserProvisioningService.CreateAsync"/> call (CAS-020 §6/§7).</summary>
public enum UserCreateOutcome
{
    /// <summary><c>201 Created</c>.</summary>
    Created,

    /// <summary>§7 V1/V2/V3 failed — <c>400 validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary><c>UQ_users_username</c> violated (§7 V1/V3, §10 SC-7) — <c>409 username-already-exists</c>.</summary>
    UsernameAlreadyExists,

    /// <summary>§7 V6 live B2 authorization failed (§10 SC-9) — <c>422 b2-key-invalid</c>.</summary>
    B2KeyInvalid
}

/// <summary>Result of one <see cref="IUserProvisioningService.CreateAsync"/> call.</summary>
public sealed record UserCreateResult
{
    public required UserCreateOutcome Outcome { get; init; }

    public CreateUserResponse? Response { get; init; }
}

/// <summary>Outcome of one <see cref="IUserProvisioningService.EditAsync"/> call (CAS-020 §6/§7).</summary>
public enum UserEditOutcome
{
    /// <summary><c>200 OK</c>.</summary>
    Updated,

    /// <summary>§7 V4/V5 failed — <c>400 validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary>Unknown <c>userId</c> — <c>404 user-not-found</c>.</summary>
    UserNotFound,

    /// <summary>§7 V6 live B2 authorization failed (§10 SC-9) — <c>422 b2-key-invalid</c>.</summary>
    B2KeyInvalid
}

/// <summary>Result of one <see cref="IUserProvisioningService.EditAsync"/> call.</summary>
public sealed record UserEditResult
{
    public required UserEditOutcome Outcome { get; init; }

    public EditUserResponse? Response { get; init; }
}

/// <summary>
/// Outcome of one <see cref="IUserProvisioningService.DeactivateAsync"/>/
/// <see cref="IUserProvisioningService.ReactivateAsync"/> call (CAS-020 §6) — shared by both,
/// mirroring <c>dbo.users_set_active</c>'s single-procedure design (§5, Karpathy P4).
/// </summary>
public enum UserSetActiveOutcome
{
    /// <summary><c>204 No Content</c>.</summary>
    Updated,

    /// <summary>Unknown <c>userId</c> — <c>404 user-not-found</c>.</summary>
    UserNotFound
}

/// <summary>Result of one deactivate/reactivate call.</summary>
public sealed record UserSetActiveResult
{
    public required UserSetActiveOutcome Outcome { get; init; }
}

/// <summary>
/// CAS-020 §3/§6 — admin-only create/edit/deactivate/reactivate of a <c>dbo.users</c> row. Every
/// operation writes exactly one audit log row (FR-23) before returning success (§12, §10 SC-6).
/// A supplied B2 key/key-ID pair is always live-validated against Backblaze B2 via
/// <see cref="IUserB2KeyAuthorizer"/> before it is persisted (§7 V6, §12) — a failed authorization
/// never reaches the repository (§10 SC-9, no partial write).
/// </summary>
public interface IUserProvisioningService
{
    Task<UserCreateResult> CreateAsync(CreateUserRequest request, CancellationToken ct);

    Task<UserEditResult> EditAsync(int userId, EditUserRequest request, CancellationToken ct);

    Task<UserSetActiveResult> DeactivateAsync(int userId, CancellationToken ct);

    Task<UserSetActiveResult> ReactivateAsync(int userId, CancellationToken ct);
}
