namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>PUT /admin/users/{userId}</c> request body (CAS-020 §6) — partial update, only supplied
/// fields change. <c>username</c> is immutable and is not accepted here (§4, §6, §15 resolved). At
/// least one field must be supplied (§7 V4); a supplied B2 key/key ID pair must arrive together
/// (§7 V5).
/// </summary>
public sealed record EditUserRequest
{
    /// <summary>Plaintext, as submitted. Never logged, never echoed in the response (§7 V5, §12).</summary>
    public string? Password { get; init; }

    /// <summary>Plaintext, as submitted. Never logged, never echoed in the response (§12).</summary>
    public string? B2ApplicationKeyId { get; init; }

    /// <summary>Plaintext, as submitted. Never logged, never echoed in the response (§12).</summary>
    public string? B2ApplicationKey { get; init; }
}
