namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Result of <c>b2_authorize_account</c> (CAS-001 §6.1), cached in memory by <c>IB2Client</c>
/// per-process (not per-session, not persisted). <see cref="AuthorizationToken"/> is a live B2
/// credential — never logged, never returned in an HTTP response, never bound to a Blazor
/// component parameter (§10 SC-5). <see cref="ToString"/> is overridden to redact it, since a
/// record's compiler-generated <c>ToString</c> would otherwise print every property, including
/// this one, if this DTO is ever passed to a logger or exception message.
/// </summary>
public sealed record B2AuthorizationInfo
{
    public required string AccountId { get; init; }

    /// <summary>Live B2 credential. Never log this value, even partially.</summary>
    public required string AuthorizationToken { get; init; }

    /// <summary>Base URL for API calls made with this authorization (§6.1).</summary>
    public required string ApiUrl { get; init; }

    /// <summary>Base URL for <c>b2_download_file_by_id</c> calls.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>The system key's granted capabilities (e.g. listFiles, readFiles, writeFiles, deleteFiles).</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// Informational only — B2 tokens are valid for at most 24h (§6.1), but re-authentication is
    /// triggered reactively on a 401 response, not proactively from this timestamp.
    /// </summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public override string ToString() =>
        $"B2AuthorizationInfo {{ AccountId = {AccountId}, ApiUrl = {ApiUrl}, " +
        $"Capabilities = [{string.Join(",", Capabilities)}], ExpiresAtUtc = {ExpiresAtUtc:O}, " +
        "AuthorizationToken = ***redacted*** }}";
}
