namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /admin/users</c> request body (CAS-020 §6). All four fields are conceptually required
/// on create (§7 V1–V3) but are modeled as nullable here since any of them may be missing/omitted
/// from the request JSON — the service layer, not model binding, is responsible for the 400
/// validation-failed mapping (§7).
/// </summary>
public sealed record CreateUserRequest
{
    public string? Username { get; init; }

    /// <summary>Plaintext, as submitted. Never logged, never echoed in the response (§7 V2, §12).</summary>
    public string? Password { get; init; }

    /// <summary>Plaintext, as submitted. Never logged, never echoed in the response (§12).</summary>
    public string? B2ApplicationKeyId { get; init; }

    /// <summary>Plaintext, as submitted. Never logged, never echoed in the response (§12).</summary>
    public string? B2ApplicationKey { get; init; }
}
