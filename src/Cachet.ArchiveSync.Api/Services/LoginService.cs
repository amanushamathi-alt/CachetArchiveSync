using System.Globalization;
using System.Security.Claims;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ILoginService"/> implementation of CAS-002 §6.1's two-stage check and claim
/// derivation.
/// </summary>
public sealed class LoginService : ILoginService
{
    // §6.1 step 3: any capability set including write/delete access maps to "Full Access";
    // everything else maps to "Read Only" (verbatim two-value simplification).
    private const string FullAccessLabel = "Full Access";
    private const string ReadOnlyLabel = "Read Only";

    private const string ActingProcessIdentity = "api-svc";

    private readonly IUserRepository _userRepository;
    private readonly IUserB2KeyAuthorizer _b2KeyAuthorizer;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<LoginService> _logger;
    private readonly PasswordHasher<object> _passwordHasher = new();

    public LoginService(
        IUserRepository userRepository,
        IUserB2KeyAuthorizer b2KeyAuthorizer,
        ICredentialProtector credentialProtector,
        IAuditLogService auditLogService,
        ILogger<LoginService> logger)
    {
        _userRepository = userRepository;
        _b2KeyAuthorizer = b2KeyAuthorizer;
        _credentialProtector = credentialProtector;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<LoginOutcome> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await _userRepository.GetByUsernameAsync(request.Username, ct);

        if (user is null || !user.IsActive || !VerifyPassword(user.PasswordHash, request.Password))
        {
            // §6.1 step 1 / API spec §3.1: the response must not distinguish "unknown username"
            // from "wrong password" — both map to InvalidCredentials, one failure reason.
            _logger.LogWarning("Login failed: {Reason}", LoginFailureReason.InvalidCredentials);

            // BLOCKED (implementer, CAS-002): dbo.audit_log.user_id is NOT NULL with an FK to
            // dbo.users (CAS-003 §4). For an unknown username there is no valid user_id to
            // attach an audit row to, so a "Login Failed" row cannot be written for that case
            // without violating the FK constraint or fabricating a user_id — neither of which
            // this spec authorizes (touches.database: false). Only audited when a matching user
            // row exists (known username, wrong password, or inactive account); flagged here for
            // CAS-003/CAS-005 schema owners rather than silently dropped.
            if (user is not null)
            {
                await _auditLogService.WriteAsync(new AuditLogEntry
                {
                    CreatedBy = ActingProcessIdentity,
                    UpdatedBy = ActingProcessIdentity,
                    UserId = user.UserId,
                    TabModule = "Auth",
                    Action = AuditAction.LoginFailed,
                    Result = AuditResult.Failure,
                    ResultDetail = "Invalid Credentials"
                }, ct);
            }

            return new LoginOutcome { Succeeded = false, FailureReason = LoginFailureReason.InvalidCredentials };
        }

        string b2KeyId;
        string b2ApplicationKey;
        try
        {
            b2KeyId = _credentialProtector.Unprotect(user.B2ApplicationKeyId);
            b2ApplicationKey = _credentialProtector.Unprotect(user.B2ApplicationKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Login failed: could not decrypt stored B2 key material for a user");
            await WriteLoginFailedAsync(user.UserId, "B2 Access Unavailable", ct);
            return new LoginOutcome { Succeeded = false, FailureReason = LoginFailureReason.B2AccessUnavailable };
        }

        B2AuthorizationInfo b2Auth;
        try
        {
            b2Auth = await _b2KeyAuthorizer.AuthorizeAsync(b2KeyId, b2ApplicationKey, ct);
        }
        catch (B2ApiException ex)
        {
            _logger.LogWarning(ex, "Login failed: {Reason}", LoginFailureReason.B2AccessUnavailable);
            await WriteLoginFailedAsync(user.UserId, "B2 Access Unavailable", ct);
            return new LoginOutcome { Succeeded = false, FailureReason = LoginFailureReason.B2AccessUnavailable };
        }

        var capability = b2Auth.Capabilities.Contains("writeFiles") || b2Auth.Capabilities.Contains("deleteFiles")
            ? FullAccessLabel
            : ReadOnlyLabel;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.Username),
            new("b2_capability", capability),
            // §6.3a: the user's session_token_version at login time — re-checked against the
            // live DB value on every request (OnValidatePrincipal); a mismatch (set by logout)
            // rejects the principal even within an otherwise-unexpired sliding window.
            new("session_ver", user.SessionTokenVersion.ToString(CultureInfo.InvariantCulture))
        };

        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = user.UserId,
            TabModule = "Auth",
            Action = AuditAction.Login,
            Result = AuditResult.Success
        }, ct);

        return new LoginOutcome
        {
            Succeeded = true,
            UserId = user.UserId,
            // NOTE: see LoginResponse.DisplayName's XML doc — no display_name column exists.
            DisplayName = user.Username,
            B2AccessLabel = capability,
            Claims = claims
        };
    }

    private Task WriteLoginFailedAsync(int userId, string resultDetail, CancellationToken ct) =>
        _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = userId,
            TabModule = "Auth",
            Action = AuditAction.LoginFailed,
            Result = AuditResult.Failure,
            ResultDetail = resultDetail
        }, ct);

    private bool VerifyPassword(string passwordHash, string suppliedPassword)
    {
        try
        {
            var result = _passwordHasher.VerifyHashedPassword(this, passwordHash, suppliedPassword);
            return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            // A malformed/foreign-format stored hash must fail closed as InvalidCredentials, not
            // surface as an unhandled 500 (defense-in-depth — provisioning's exact hash format is
            // out of CAS-002's scope, §4).
            return false;
        }
    }
}
