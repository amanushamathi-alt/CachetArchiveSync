namespace Cachet.ArchiveSync.Api.IntegrationTests.Auth;

/// <summary>
/// Minimal, manually-advanced <see cref="TimeProvider"/> so <c>CookieAuthenticationOptions</c>'s
/// sliding-expiration math (CAS-002 §6.2/§10 SC-2) can be tested deterministically — advancing
/// hours of "elapsed time" without the test actually waiting — instead of pulling in the
/// Microsoft.Extensions.TimeProvider.Testing package for one small type.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
