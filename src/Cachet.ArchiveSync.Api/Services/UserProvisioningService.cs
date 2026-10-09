using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IUserProvisioningService"/> implementation backing <c>POST /admin/users</c>,
/// <c>PUT /admin/users/{userId}</c>, <c>PATCH /admin/users/{userId}/deactivate</c>, and
/// <c>PATCH /admin/users/{userId}/reactivate</c> (CAS-020 §3, §6, §7, §12).
/// </summary>
public sealed class UserProvisioningService : IUserProvisioningService
{
    // §5, §12, §15 resolved — the shared admin token carries no per-person identity, so every
    // row this service writes (dbo.users.created_by/updated_by and the audit row's
    // created_by/updated_by) uses this fixed constant, mirroring LoginService's
    // ActingProcessIdentity ("api-svc") pattern.
    private const string ActingIdentity = "admin-provisioning";

    // CAS-005 §7 V1's fixed tab_module value set has no "Admin" member (AuditLogService.cs); "Auth"
    // is the closest existing module for user-account management (mirrors LoginService's own tab)
    // and avoids widening AuditLogService's fixed set for a single new caller (Karpathy P3/P4).
    private const string TabModule = "Auth";

    private const int MinimumPasswordLength = 8;
    private const int MaximumUsernameLength = 50;

    private readonly IUserRepository _userRepository;
    private readonly IUserB2KeyAuthorizer _b2KeyAuthorizer;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<UserProvisioningService> _logger;
    private readonly PasswordHasher<object> _passwordHasher = new();

    public UserProvisioningService(
        IUserRepository userRepository,
        IUserB2KeyAuthorizer b2KeyAuthorizer,
        ICredentialProtector credentialProtector,
        IAuditLogService auditLogService,
        ILogger<UserProvisioningService> logger)
    {
        _userRepository = userRepository;
        _b2KeyAuthorizer = b2KeyAuthorizer;
        _credentialProtector = credentialProtector;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<UserCreateResult> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        // §7 V1: username required, non-empty, <= 50 chars.
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > MaximumUsernameLength)
        {
            return Fail(UserCreateOutcome.ValidationFailed, "Create", "V1 username");
        }

        // §7 V2: password required on create, minimum 8 characters.
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < MinimumPasswordLength)
        {
            return Fail(UserCreateOutcome.ValidationFailed, "Create", "V2 password");
        }

        // §7 V3: b2ApplicationKeyId and b2ApplicationKey both required on create.
        if (string.IsNullOrWhiteSpace(request.B2ApplicationKeyId) || string.IsNullOrWhiteSpace(request.B2ApplicationKey))
        {
            return Fail(UserCreateOutcome.ValidationFailed, "Create", "V3 B2 key");
        }

        // §7 V6/§12: live B2 authorization before anything is persisted.
        if (!await AuthorizeB2KeyAsync(request.B2ApplicationKeyId, request.B2ApplicationKey, ct))
        {
            return Fail(UserCreateOutcome.B2KeyInvalid, "Create", "V6 B2 key invalid");
        }

        var passwordHash = _passwordHasher.HashPassword(this, request.Password);
        var b2KeyIdCiphertext = _credentialProtector.Protect(request.B2ApplicationKeyId);
        var b2KeyCiphertext = _credentialProtector.Protect(request.B2ApplicationKey);

        var insertResult = await _userRepository.InsertAsync(
            ActingIdentity, ActingIdentity, request.Username, passwordHash, b2KeyIdCiphertext, b2KeyCiphertext, ct);

        if (insertResult.Outcome == UserInsertOutcome.UsernameAlreadyExists)
        {
            return Fail(UserCreateOutcome.UsernameAlreadyExists, "Create", "username-already-exists");
        }

        var userId = insertResult.UserId!.Value;
        var createdAt = DateTimeOffset.UtcNow;

        // §12: audit-before-response invariant.
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingIdentity,
            UpdatedBy = ActingIdentity,
            UserId = userId,
            TabModule = TabModule,
            Action = AuditAction.UserCreated,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation("Admin {Action} succeeded for user {UserId}", "Create", userId);

        return new UserCreateResult
        {
            Outcome = UserCreateOutcome.Created,
            Response = new CreateUserResponse
            {
                UserId = userId,
                Username = request.Username,
                CreatedAt = createdAt
            }
        };
    }

    public async Task<UserEditResult> EditAsync(int userId, EditUserRequest request, CancellationToken ct)
    {
        var b2KeyIdSupplied = !string.IsNullOrWhiteSpace(request.B2ApplicationKeyId);
        var b2KeySupplied = !string.IsNullOrWhiteSpace(request.B2ApplicationKey);
        var passwordSupplied = !string.IsNullOrEmpty(request.Password);

        // §7 V4: at least one field must be supplied.
        if (!passwordSupplied && !b2KeyIdSupplied && !b2KeySupplied)
        {
            return FailEdit(UserEditOutcome.ValidationFailed, "Edit", "V4 empty body");
        }

        // §7 V5: b2ApplicationKeyId/b2ApplicationKey must be supplied together; password must
        // meet the same 8-character minimum as V2 if supplied.
        if (b2KeyIdSupplied != b2KeySupplied)
        {
            return FailEdit(UserEditOutcome.ValidationFailed, "Edit", "V5 partial B2 key");
        }

        if (passwordSupplied && request.Password!.Length < MinimumPasswordLength)
        {
            return FailEdit(UserEditOutcome.ValidationFailed, "Edit", "V5 password too short");
        }

        // 404 before 422 — an unauthorized/nonexistent target should not incur a live external B2
        // call. Also gives us Username for the response (username is not accepted by this
        // endpoint, §4, so it must be read back).
        var existingUser = await _userRepository.GetByIdAsync(userId, ct);
        if (existingUser is null)
        {
            return FailEdit(UserEditOutcome.UserNotFound, "Edit", "user-not-found");
        }

        // §7 V6/§12: live B2 authorization before anything is persisted, same as create.
        if (b2KeyIdSupplied && !await AuthorizeB2KeyAsync(request.B2ApplicationKeyId!, request.B2ApplicationKey!, ct))
        {
            return FailEdit(UserEditOutcome.B2KeyInvalid, "Edit", "V6 B2 key invalid");
        }

        var passwordHash = passwordSupplied ? _passwordHasher.HashPassword(this, request.Password!) : null;
        var b2KeyIdCiphertext = b2KeyIdSupplied ? _credentialProtector.Protect(request.B2ApplicationKeyId!) : null;
        var b2KeyCiphertext = b2KeySupplied ? _credentialProtector.Protect(request.B2ApplicationKey!) : null;

        var rowsAffected = await _userRepository.UpdateAsync(
            userId, ActingIdentity, passwordHash, b2KeyIdCiphertext, b2KeyCiphertext, ct);

        if (rowsAffected == 0)
        {
            // Defensive — existence was already confirmed above; only reachable on a concurrent
            // change this spec has no path to make happen (no user-delete endpoint, §4).
            return FailEdit(UserEditOutcome.UserNotFound, "Edit", "user-not-found");
        }

        var updatedAt = DateTimeOffset.UtcNow;

        // §12: audit-before-response invariant.
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingIdentity,
            UpdatedBy = ActingIdentity,
            UserId = userId,
            TabModule = TabModule,
            Action = AuditAction.UserUpdated,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation("Admin {Action} succeeded for user {UserId}", "Edit", userId);

        return new UserEditResult
        {
            Outcome = UserEditOutcome.Updated,
            Response = new EditUserResponse
            {
                UserId = userId,
                Username = existingUser.Username,
                UpdatedAt = updatedAt
            }
        };
    }

    public Task<UserSetActiveResult> DeactivateAsync(int userId, CancellationToken ct) =>
        SetActiveAsync(userId, isActive: false, "Deactivate", AuditAction.UserDeactivated, ct);

    public Task<UserSetActiveResult> ReactivateAsync(int userId, CancellationToken ct) =>
        SetActiveAsync(userId, isActive: true, "Reactivate", AuditAction.UserReactivated, ct);

    private async Task<UserSetActiveResult> SetActiveAsync(
        int userId, bool isActive, string actionLabel, AuditAction auditAction, CancellationToken ct)
    {
        var rowsAffected = await _userRepository.SetActiveAsync(userId, ActingIdentity, isActive, ct);

        if (rowsAffected == 0)
        {
            _logger.LogWarning("Admin {Action} failed: {Reason}", actionLabel, "user-not-found");
            return new UserSetActiveResult { Outcome = UserSetActiveOutcome.UserNotFound };
        }

        // §12: audit-before-response invariant.
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingIdentity,
            UpdatedBy = ActingIdentity,
            UserId = userId,
            TabModule = TabModule,
            Action = auditAction,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation("Admin {Action} succeeded for user {UserId}", actionLabel, userId);

        return new UserSetActiveResult { Outcome = UserSetActiveOutcome.Updated };
    }

    /// <summary>§7 V6/§12 — live authorization of a caller-supplied, not-yet-persisted B2 key pair,
    /// the same call <c>LoginService</c> makes at sign-in. Never logs the raw key/key-ID (§11).</summary>
    private async Task<bool> AuthorizeB2KeyAsync(string keyId, string applicationKey, CancellationToken ct)
    {
        try
        {
            await _b2KeyAuthorizer.AuthorizeAsync(keyId, applicationKey, ct);
            return true;
        }
        catch (B2ApiException ex)
        {
            _logger.LogWarning(ex, "Admin B2 key authorization failed");
            return false;
        }
    }

    private UserCreateResult Fail(UserCreateOutcome outcome, string actionLabel, string reason)
    {
        _logger.LogWarning("Admin {Action} failed: {Reason}", actionLabel, reason);
        return new UserCreateResult { Outcome = outcome };
    }

    private UserEditResult FailEdit(UserEditOutcome outcome, string actionLabel, string reason)
    {
        _logger.LogWarning("Admin {Action} failed: {Reason}", actionLabel, reason);
        return new UserEditResult { Outcome = outcome };
    }
}
