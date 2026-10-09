using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Live <c>b2_authorize_account</c> validation for one specific, caller-supplied B2 key
/// (CAS-002 §6.1 step 2) — deliberately separate from <see cref="IB2Client"/>.
///
/// IMPLEMENTER NOTE / DEVIATION (CAS-002, not a CAS-001 change): <see cref="IB2Client.AuthorizeAsync"/>
/// takes no credential parameter by design — CAS-001 §7 V1 requires <c>B2Client</c> to be
/// constructed only from the server-held system key in configuration, specifically so a
/// per-request/browser-supplied key can never reach it. That is exactly the operational client
/// CAS-002 §6.5 says must stay system-key-only. But CAS-002 §6.1 step 2 requires authorizing each
/// individual user's *own* stored key at login time — a different key per call, not the system
/// key — which the existing <see cref="IB2Client"/> contract cannot express without weakening
/// CAS-001's own invariant. Extending <see cref="IB2Client"/>'s method surface is CAS-001's file,
/// not CAS-002's, so this minimal, single-purpose seam is introduced here instead. Flagged for
/// reviewer sign-off as a cross-spec architecture gap neither CAS-001 nor CAS-002 fully resolves
/// (CAS-002 §17 "Surprising Connections" territory).
/// </summary>
public interface IUserB2KeyAuthorizer
{
    /// <summary>
    /// Calls <c>b2_authorize_account</c> with the given, already-decrypted key pair and returns
    /// its capability set. Throws <see cref="Exceptions.B2ApiException"/> (or a subtype) on any
    /// non-success response — the caller (CAS-002 §6.1 step 2) maps that to the
    /// <c>b2-access-unavailable</c> failure. Never logs <paramref name="applicationKey"/> or the
    /// returned authorization token (CAS-002 §11/§12).
    /// </summary>
    Task<B2AuthorizationInfo> AuthorizeAsync(string keyId, string applicationKey, CancellationToken ct);
}
