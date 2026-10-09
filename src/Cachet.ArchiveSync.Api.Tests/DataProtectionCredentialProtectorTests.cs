using Cachet.ArchiveSync.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="DataProtectionCredentialProtector"/> (CAS-002 §6.1 step 2 —
/// decrypts <c>dbo.users.b2_application_key(_id)</c> ciphertext; see <c>ICredentialProtector</c>'s
/// XML doc for why this concrete provider is a best-effort implementation choice, not a
/// spec-confirmed one). The Data Protection API's encrypt/decrypt round trip is pure in-memory
/// cryptography with no DB or file-system dependency — <see cref="EphemeralDataProtectionProvider"/>
/// gives a real (non-persisted, process-lifetime) key ring, so these tests exercise the class's
/// actual <see cref="IDataProtector"/> usage rather than a mocked one.
/// </summary>
public sealed class DataProtectionCredentialProtectorTests
{
    private const string Purpose = "Cachet.ArchiveSync.B2CredentialProtection";

    private static IDataProtectionProvider CreateProvider() => new EphemeralDataProtectionProvider();

    [Fact]
    public void Test_Unprotect_RoundTripsCiphertextProducedByTheSamePurpose()
    {
        // Arrange — a ciphertext produced exactly the way the (out-of-scope, CAS-002 §4)
        // provisioning endpoint must produce it: same provider, same purpose string.
        var provider = CreateProvider();
        var plaintext = "K0000000000000RawApplicationKeyValueNeverLeak";
        var ciphertext = provider.CreateProtector(Purpose).Protect(plaintext);

        var sut = new DataProtectionCredentialProtector(provider);

        // Act
        var result = sut.Unprotect(ciphertext);

        // Assert
        result.Should().Be(plaintext);
    }

    [Fact]
    public void Test_Unprotect_CiphertextFromDifferentPurpose_ThrowsCryptographicException()
    {
        // Arrange — CAS-002 §6.6 uses a distinct purpose string for the session cookie's own
        // protector, from the same key ring. A value protected under that different purpose (or
        // any purpose other than this class's own) must not silently decrypt — Data Protection
        // purpose strings are part of the key derivation, so this is real cryptographic isolation,
        // not merely a naming convention.
        var provider = CreateProvider();
        var ciphertextFromWrongPurpose = provider.CreateProtector("Some.Other.Purpose").Protect("irrelevant");

        var sut = new DataProtectionCredentialProtector(provider);

        // Act
        var act = () => sut.Unprotect(ciphertextFromWrongPurpose);

        // Assert
        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void Test_Unprotect_MalformedCiphertext_ThrowsCryptographicException()
    {
        // Arrange — defends against a corrupted/truncated dbo.users column value.
        var provider = CreateProvider();
        var sut = new DataProtectionCredentialProtector(provider);

        // Act
        var act = () => sut.Unprotect("not-a-real-protected-payload");

        // Assert
        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    // ---- CAS-020 §3: new Protect(string) method, added for the admin-provisioning write path -----

    [Fact]
    public void Test_ProtectThenUnprotect_RoundTrips()
    {
        // Arrange — CAS-020 §6/§12: the admin-provisioning write path calls Protect(plaintext) with
        // the SAME provider/purpose LoginService's Unprotect already decrypts with, so a value
        // provisioned via this class's own Protect must round-trip through this class's own
        // Unprotect (SC-4's decryption-compatibility invariant, proven at the unit level here and
        // end-to-end via a real login in UsersAdminControllerTests).
        var provider = CreateProvider();
        var sut = new DataProtectionCredentialProtector(provider);
        const string plaintext = "K0043xxxxxxxxxxxxxxxxxxxxxxxxxxxx";

        // Act
        var ciphertext = sut.Protect(plaintext);
        var roundTripped = sut.Unprotect(ciphertext);

        // Assert
        ciphertext.Should().NotBe(plaintext);
        roundTripped.Should().Be(plaintext);
    }

    [Fact]
    public void Test_Unprotect_DifferentPlaintexts_ProduceIndependentRoundTrips()
    {
        // Arrange
        var provider = CreateProvider();
        var protector = provider.CreateProtector(Purpose);
        var sut = new DataProtectionCredentialProtector(provider);

        var keyIdCiphertext = protector.Protect("0000000000000000000000001");
        var keyCiphertext = protector.Protect("K000RawApplicationKeyValueNeverLeak");

        // Act
        var keyId = sut.Unprotect(keyIdCiphertext);
        var key = sut.Unprotect(keyCiphertext);

        // Assert — the two decrypt independently to their own original plaintext (no cross-talk),
        // matching how LoginService calls Unprotect twice for the key id and the key itself.
        keyId.Should().Be("0000000000000000000000001");
        key.Should().Be("K000RawApplicationKeyValueNeverLeak");
        keyId.Should().NotBe(key);
    }
}
