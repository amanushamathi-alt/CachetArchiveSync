namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Application-layer encrypt/decrypt for <c>dbo.users.b2_application_key</c> /
/// <c>b2_application_key_id</c> ciphertext (CAS-003 §4; Security Analysis §2.1).
///
/// IMPLEMENTER NOTE (CAS-002): no document (CAS-001, CAS-002, or CAS-003) names a concrete
/// encryption provider — CAS-003 §15 explicitly defers this ("confirm against the actual
/// encryption provider's ciphertext length before/at implementation"), and Security Analysis
/// §2.1 only offers "e.g., ASP.NET Core Data Protection" as an illustrative example, not a
/// binding decision. CAS-002 §6.1 step 2 assumes this decryption already exists as a dependency.
/// Since CAS-002 touches.database: false and owns no migration, this interface's concrete
/// implementation (an ASP.NET Core Data Protection-backed protector, reusing the key ring already
/// wired for CAS-002 §6.6's cookie scheme, under its own distinct purpose string) is a best-effort
/// choice to keep the login flow executable — not a spec-confirmed decision. The provisioning
/// endpoint (CAS-002 §4, out of scope here) must encrypt with this exact same purpose string for
/// stored rows to decrypt correctly; flag for reviewer/db-author sign-off.
/// </summary>
public interface ICredentialProtector
{
    string Unprotect(string ciphertext);

    /// <summary>
    /// Encrypts a raw B2 key/key-ID value (CAS-020 §3, §12) with the same purpose string
    /// <see cref="Unprotect"/> decrypts with, so a value provisioned here round-trips correctly at
    /// login. First caller of this direction — CAS-020 is the first writer to
    /// <c>dbo.users.b2_application_key(_id)</c>, so no prior code path needed to encrypt rather
    /// than decrypt.
    /// </summary>
    string Protect(string plaintext);
}
