using System.Security.Claims;
using Cachet.ArchiveSync.Core.Enums;

namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Result of <see cref="Interfaces.ILoginService.LoginAsync"/> — kept separate from
/// <see cref="LoginResponse"/> so the service layer stays independently unit-testable (CAS-002
/// §13, <c>LoginServiceTests</c>) without needing an <c>HttpContext</c> to inspect the claims a
/// successful login would issue (SC-1).
/// </summary>
public sealed record LoginOutcome
{
    public required bool Succeeded { get; init; }

    public LoginFailureReason? FailureReason { get; init; }

    public int? UserId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>"Full Access" or "Read Only" (CAS-002 §6.1 step 3).</summary>
    public string? B2AccessLabel { get; init; }

    /// <summary>
    /// The exact claim set §6.1 step 4 specifies. Only present on success. Never includes
    /// <c>b2_application_key_id</c>, <c>b2_application_key</c>, or <c>password_hash</c> (SC-1).
    /// </summary>
    public IReadOnlyList<Claim>? Claims { get; init; }
}
