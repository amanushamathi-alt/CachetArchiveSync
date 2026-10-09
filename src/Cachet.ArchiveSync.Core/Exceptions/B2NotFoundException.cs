namespace Cachet.ArchiveSync.Core.Exceptions;

/// <summary>
/// A B2 API call returned 404 (<c>file_not_present</c>) — distinct from
/// <see cref="B2ApiException"/> so §6.3's cross-check exclusion path can catch this specifically
/// rather than pattern-matching on status codes at every call site.
/// </summary>
public sealed class B2NotFoundException : B2ApiException
{
    public B2NotFoundException(string message)
        : base(404, "file_not_present", message)
    {
    }
}
