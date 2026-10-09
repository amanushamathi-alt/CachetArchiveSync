namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>POST /admin/users</c> <c>201 Created</c> response body (CAS-020 §6). Never carries
/// <c>password</c>, <c>b2ApplicationKeyId</c>, or <c>b2ApplicationKey</c> in any form (§6, §10 SC-2).
/// </summary>
public sealed record CreateUserResponse
{
    public required int UserId { get; init; }

    public required string Username { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
