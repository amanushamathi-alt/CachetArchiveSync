using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Server-side Backblaze B2 client (CAS-001 §6.1). One shared implementation used by both the
/// Api (FR-08 cross-check calls) and the Worker (mirror population §6.2, delete §6.4) — never
/// constructed with anything other than the server-held system key from configuration (§7 V1).
/// Exposes exactly the seven named B2 operations §6.1 identifies as needed: <c>b2_authorize_account</c>,
/// <c>b2_list_file_names</c>, <c>b2_list_file_versions</c>, <c>b2_get_file_info</c>,
/// <c>b2_download_file_by_id</c>, <c>b2_get_upload_url</c> + <c>b2_upload_file</c>, and
/// <c>b2_delete_file_version</c>. Implementations must never log the returned authorization or
/// upload tokens, even partially (§10 SC-5).
/// </summary>
public interface IB2Client
{
    /// <summary>
    /// Calls <c>b2_authorize_account</c> against the server-held system key and refreshes the
    /// client's cached authorization. Other methods on this interface call this implicitly (and
    /// transparently re-authenticate on a 401/expired-token response) — callers do not need to
    /// call this themselves before every operation (§6.1 token lifecycle).
    /// </summary>
    Task<B2AuthorizationInfo> AuthorizeAsync(CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_list_file_names</c> against the configured bucket, paginated via
    /// <paramref name="startFileName"/>/<see cref="B2ListFileNamesResult.NextFileName"/>.
    /// <paramref name="maxFileCount"/> is capped at 1000 per call (§6.2 step 2).
    /// </summary>
    Task<B2ListFileNamesResult> ListFileNamesAsync(
        string? startFileName, int maxFileCount, CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_list_file_versions</c> against the configured bucket, enumerating every
    /// version of objects matching <paramref name="prefix"/> (§6.4 Step B).
    /// </summary>
    Task<B2ListFileVersionsResult> ListFileVersionsAsync(
        string? startFileName, string? prefix, int maxFileCount, CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_get_file_info</c> for a specific <paramref name="fileId"/> (§6.3 step 2).
    /// Throws <see cref="Exceptions.B2NotFoundException"/> on a 404 (<c>file_not_present</c>)
    /// response rather than returning null, so callers cannot accidentally treat a missing file
    /// as a valid result.
    /// </summary>
    Task<B2FileInfo> GetFileInfoAsync(string fileId, CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_download_file_by_id</c> (§6.2 step 4). The caller owns disposing the returned
    /// <see cref="B2DownloadedFile"/> (and therefore its content stream).
    /// </summary>
    Task<B2DownloadedFile> DownloadFileByIdAsync(string fileId, CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_get_upload_url</c> for the configured bucket (§6.1). The returned
    /// <see cref="B2UploadUrlInfo"/> is single-use-oriented per B2's own guidance — callers should
    /// request a fresh one if <see cref="UploadFileAsync"/> fails.
    /// </summary>
    Task<B2UploadUrlInfo> GetUploadUrlAsync(CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_upload_file</c> against a previously obtained <paramref name="uploadUrl"/>.
    /// </summary>
    Task<B2FileInfo> UploadFileAsync(
        B2UploadUrlInfo uploadUrl,
        string fileName,
        Stream content,
        string contentSha1,
        long contentLength,
        CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_delete_file_version</c> for exactly one specific version
    /// (<paramref name="fileName"/> + <paramref name="fileId"/>) — not "the file" as a whole
    /// (§6.4 Step B). Callers enumerating all versions of an object must call this once per
    /// version returned by <see cref="ListFileVersionsAsync"/>.
    /// </summary>
    Task DeleteFileVersionAsync(string fileName, string fileId, CancellationToken ct);
}
