using System.Text.Json.Serialization;

namespace Cachet.ArchiveSync.Api.B2;

// Raw JSON shapes for the Backblaze B2 native REST API (b2api/v2 — CAS-001 §6.1: in-house
// wrapper, no third-party SDK). Intentionally kept separate from Cachet.ArchiveSync.Core's DTOs
// (Cachet.ArchiveSync.Api.B2.B2Client maps between the two) so B2's wire format never leaks into
// Core, per the clean-architecture skill ("Core holds ... interfaces only").

internal sealed class B2WireAuthorizeAccountResponse
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; init; } = string.Empty;

    [JsonPropertyName("authorizationToken")]
    public string AuthorizationToken { get; init; } = string.Empty;

    [JsonPropertyName("apiUrl")]
    public string ApiUrl { get; init; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("allowed")]
    public B2WireAllowed? Allowed { get; init; }
}

internal sealed class B2WireAllowed
{
    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; init; }
}

internal sealed class B2WireListBucketsResponse
{
    [JsonPropertyName("buckets")]
    public List<B2WireBucket> Buckets { get; init; } = new();
}

internal sealed class B2WireBucket
{
    [JsonPropertyName("bucketId")]
    public string BucketId { get; init; } = string.Empty;

    [JsonPropertyName("bucketName")]
    public string BucketName { get; init; } = string.Empty;
}

internal sealed class B2WireListFileNamesResponse
{
    [JsonPropertyName("files")]
    public List<B2WireFile> Files { get; init; } = new();

    [JsonPropertyName("nextFileName")]
    public string? NextFileName { get; init; }
}

internal sealed class B2WireListFileVersionsResponse
{
    [JsonPropertyName("files")]
    public List<B2WireFile> Files { get; init; } = new();

    [JsonPropertyName("nextFileName")]
    public string? NextFileName { get; init; }

    [JsonPropertyName("nextFileId")]
    public string? NextFileId { get; init; }
}

internal sealed class B2WireFile
{
    [JsonPropertyName("fileId")]
    public string FileId { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("contentSha1")]
    public string? ContentSha1 { get; init; }

    [JsonPropertyName("contentLength")]
    public long? ContentLength { get; init; }

    // b2_get_file_info / b2_upload_file return "size", list_file_names/versions return "contentLength".
    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("uploadTimestamp")]
    public long UploadTimestamp { get; init; }
}

internal sealed class B2WireGetUploadUrlResponse
{
    [JsonPropertyName("bucketId")]
    public string BucketId { get; init; } = string.Empty;

    [JsonPropertyName("uploadUrl")]
    public string UploadUrl { get; init; } = string.Empty;

    [JsonPropertyName("authorizationToken")]
    public string AuthorizationToken { get; init; } = string.Empty;
}

/// <summary>B2's error envelope, returned on any non-2xx response.</summary>
internal sealed class B2WireErrorResponse
{
    [JsonPropertyName("status")]
    public int Status { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
