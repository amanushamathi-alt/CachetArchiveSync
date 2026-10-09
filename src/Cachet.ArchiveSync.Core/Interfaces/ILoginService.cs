using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// The two-stage <c>POST /login</c> check (username/password, then live B2 key validation) and
/// session-claim derivation (CAS-002 §6.1). Does not itself issue the authentication cookie —
/// that is an ASP.NET Core <c>HttpContext.SignInAsync</c> call the controller makes with the
/// claims this service returns, keeping this service testable without an <c>HttpContext</c>
/// (CAS-002 §13, <c>LoginServiceTests</c>).
/// </summary>
public interface ILoginService
{
    /// <summary>
    /// Writes the CAS-002 §6.1/§12 "Login" or "Login Failed" audit row before returning, per the
    /// audit-before-response invariant (API Design Spec §1.6) — the caller does not need to audit
    /// this call itself.
    /// </summary>
    Task<LoginOutcome> LoginAsync(LoginRequest request, CancellationToken ct);
}
