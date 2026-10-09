namespace Cachet.ArchiveSync.Core.Enums;

/// <summary>
/// Fixed failure-reason catalog for <c>POST /login</c> (API Design Spec §3.1 error table). The
/// message returned to the caller must never distinguish "unknown username" from "wrong
/// password" — both map to <see cref="InvalidCredentials"/> (CAS-002 §6.1, API spec §3.1).
/// </summary>
public enum LoginFailureReason
{
    InvalidCredentials,
    B2AccessUnavailable
}
