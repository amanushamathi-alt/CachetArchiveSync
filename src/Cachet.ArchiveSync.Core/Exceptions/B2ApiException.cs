namespace Cachet.ArchiveSync.Core.Exceptions;

/// <summary>
/// A B2 API call returned a non-success status. <see cref="Message"/> and every property here
/// are built only from the B2 error envelope's <c>code</c>/<c>message</c> fields and the HTTP
/// status — never from request headers, so this can never carry a B2 key value (§10 SC-5).
/// </summary>
public class B2ApiException : Exception
{
    public int StatusCode { get; }

    /// <summary>B2's own short error code (e.g. <c>file_not_present</c>, <c>bad_auth_token</c>), if present.</summary>
    public string? B2Code { get; }

    public B2ApiException(int statusCode, string? b2Code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        B2Code = b2Code;
    }
}
