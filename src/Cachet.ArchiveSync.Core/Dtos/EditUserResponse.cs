namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>PUT /admin/users/{userId}</c> <c>200 OK</c> response body (CAS-020 §6). Never carries
/// <c>password</c>, <c>b2ApplicationKeyId</c>, or <c>b2ApplicationKey</c> in any form (§6, §10 SC-2).
/// </summary>
public sealed record EditUserResponse
{
    public required int UserId { get; init; }

    public required string Username { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
