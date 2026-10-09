namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// The bucket's folder prefix (<c>dbo.odrive_bucket_mapping.b2_path_prefix</c>, CAS-031/CAS-033),
/// read for the configured bucket. Introduced by CAS-043 (CAS-032, which first described this
/// interface, was superseded and never implemented).
/// </summary>
public interface IB2PathPrefixProvider
{
    /// <summary>
    /// Normalized prefix (<c>/</c>-separated, no leading or trailing <c>/</c>; <c>""</c> = bucket
    /// root), or <c>null</c> when no active mapping row exists for <paramref name="bucketName"/>.
    /// </summary>
    Task<string?> GetPrefixAsync(string bucketName, CancellationToken ct);
}
