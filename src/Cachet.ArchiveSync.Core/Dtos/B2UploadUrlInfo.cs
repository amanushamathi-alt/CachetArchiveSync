namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Result of <c>b2_get_upload_url</c> (CAS-001 §6.1), passed to a subsequent
/// <c>IB2Client.UploadFileAsync</c> call. <see cref="AuthorizationToken"/> here is an
/// upload-specific credential, distinct from <see cref="B2AuthorizationInfo.AuthorizationToken"/>,
/// and is just as sensitive — never logged (§10 SC-5). <see cref="ToString"/> is overridden for
/// the same reason as <see cref="B2AuthorizationInfo"/>.
/// </summary>
public sealed record B2UploadUrlInfo
{
    public required string BucketId { get; init; }

    public required string UploadUrl { get; init; }

    /// <summary>Upload-specific credential. Never log this value, even partially.</summary>
    public required string AuthorizationToken { get; init; }

    public override string ToString() =>
        $"B2UploadUrlInfo {{ BucketId = {BucketId}, UploadUrl = {UploadUrl}, " +
        "AuthorizationToken = ***redacted*** }}";
}
