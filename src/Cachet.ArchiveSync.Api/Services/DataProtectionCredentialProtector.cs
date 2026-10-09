using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// See <see cref="ICredentialProtector"/>'s XML doc for why this specific mechanism was chosen
/// and why it is flagged as a best-effort implementation choice rather than a spec-confirmed one.
/// Uses a purpose string distinct from CAS-002 §6.6's session-cookie protector, from the same
/// shared <see cref="IDataProtectionProvider"/>/key ring already configured in <c>Program.cs</c>.
/// </summary>
public sealed class DataProtectionCredentialProtector : ICredentialProtector
{
    private const string Purpose = "Cachet.ArchiveSync.B2CredentialProtection";

    private readonly IDataProtector _protector;

    public DataProtectionCredentialProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);

    /// <summary>CAS-020 §3/§12 — encrypts with the same purpose string <see cref="Unprotect"/>
    /// decrypts with, so a provisioned value round-trips correctly at login (§10 SC-4).</summary>
    public string Protect(string plaintext) => _protector.Protect(plaintext);
}
