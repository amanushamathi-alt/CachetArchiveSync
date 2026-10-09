namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /login</c> request body (CAS-002 §6.1; shape fixed by API Design Spec §3.1 — CAS-002
/// §4 explicitly defers the JSON contract to that document and does not restate it here).
/// </summary>
public sealed record LoginRequest
{
    public required string Username { get; init; }

    /// <summary>Plaintext, as submitted by the sign-in form. Never logged (CAS-002 §11/§12).</summary>
    public required string Password { get; init; }
}
