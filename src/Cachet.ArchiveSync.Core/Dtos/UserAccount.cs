namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Entity mirror of one <c>dbo.users</c> row (CAS-003 §4) — only the columns CAS-002's login
/// flow needs (CAS-002 §5). <see cref="PasswordHash"/>, <see cref="B2ApplicationKeyId"/>, and
/// <see cref="B2ApplicationKey"/> must never leave <see cref="Services"/>-layer code: never
/// logged, never mapped onto a claim, cookie, or HTTP response (CAS-002 §6.1 step 4, §12).
/// </summary>
public sealed record UserAccount
{
    public required int UserId { get; init; }

    public required string Username { get; init; }

    /// <summary>Salted hash (CAS-002 implementer note: verified via ASP.NET Core Identity's
    /// <c>PasswordHasher</c>/PBKDF2 — one of the three algorithms Security Analysis §2.1 names as
    /// acceptable; no document picks a single one, so this is a documented implementation choice,
    /// not a spec-confirmed decision).</summary>
    public required string PasswordHash { get; init; }

    /// <summary>Ciphertext — application-layer encrypted (CAS-003 §4). Never logged.</summary>
    public required string B2ApplicationKeyId { get; init; }

    /// <summary>Ciphertext — application-layer encrypted (CAS-003 §4). Never logged.</summary>
    public required string B2ApplicationKey { get; init; }

    public required bool IsActive { get; init; }

    /// <summary>Per-user logout counter (CAS-002 §6.3a). Embedded in the login-time
    /// <c>session_ver</c> claim; re-checked against the live DB value on every request via
    /// <c>OnValidatePrincipal</c>. Never logged or exposed outside the auth surface.</summary>
    public required int SessionTokenVersion { get; init; }
}
