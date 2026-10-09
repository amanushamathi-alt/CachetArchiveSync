namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /login</c> <c>200 OK</c> response body (API Design Spec §3.1, restated verbatim per
/// CAS-002 §4/§6.1 step 5). Never carries the session/cookie value, the raw B2 key, key ID, or
/// password hash (CAS-002 §6.1 step 5, §12).
/// </summary>
public sealed record LoginResponse
{
    public required int UserId { get; init; }

    /// <summary>
    /// NOTE (implementer, CAS-002): the API Design Spec §3.1 example shows a formatted display
    /// string ("M. Alvarez — Cachet") that is not derivable from any column on <c>dbo.users</c>
    /// (CAS-003 §4 — no <c>display_name</c> column, and no document defines a derivation rule).
    /// This is a genuine spec gap outside CAS-002's own scope (Users table schema is explicitly
    /// out of scope, CAS-002 §4). Using <c>username</c> verbatim as a stopgap rather than
    /// fabricating a formatting rule — flag for client/DB clarification, do not treat this value
    /// as spec-confirmed.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>"Full Access" or "Read Only" — derived once at login (CAS-002 §6.1 step 3).</summary>
    public required string B2AccessLabel { get; init; }
}
