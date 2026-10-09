using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-020's admin-provisioning DTOs (<see cref="CreateUserResponse"/>, <see cref="EditUserRequest"/>,
/// <see cref="EditUserResponse"/>) and repository/service result records (<see cref="UserInsertResult"/>,
/// <see cref="UserCreateResult"/>, <see cref="UserEditResult"/>, <see cref="UserSetActiveResult"/>) are
/// all C# records. Every other test in this project exercises them only through their init-property
/// constructor, so the compiler-synthesized value-equality members (<c>Equals</c>,
/// <c>GetHashCode</c>, <c>ToString</c>/<c>PrintMembers</c>, and the <c>with</c>-expression copy
/// constructor) are otherwise never hit, which is exactly what the coverage gate flagged for these
/// files. These tests target those specific synthesized members — not the properties themselves,
/// already proven by <see cref="UserProvisioningServiceTests"/> and the integration tests — one
/// assertion group per record, per Karpathy P3 (smallest diff that closes the gap).
/// </summary>
public sealed class AdminProvisioningRecordContractTests
{
    [Fact]
    public void Test_CreateUserResponse_RecordMembers_AreValueBased()
    {
        var original = new CreateUserResponse { UserId = 1, Username = "m.alvarez", CreatedAt = DateTimeOffset.UnixEpoch };
        var copy = original with { };
        var different = original with { Username = "someone.else" };

        copy.Should().Be(original);
        copy.GetHashCode().Should().Be(original.GetHashCode());
        different.Should().NotBe(original);
        original.ToString().Should().Contain(nameof(CreateUserResponse.Username));
    }

    [Fact]
    public void Test_EditUserRequest_RecordMembers_AreValueBased()
    {
        var original = new EditUserRequest { Password = "a-new-strong-password", B2ApplicationKeyId = "id", B2ApplicationKey = "key" };
        var copy = original with { };
        var different = original with { Password = null };

        copy.Should().Be(original);
        copy.GetHashCode().Should().Be(original.GetHashCode());
        different.Should().NotBe(original);
        original.ToString().Should().Contain(nameof(EditUserRequest.B2ApplicationKeyId));
    }

    [Fact]
    public void Test_EditUserResponse_RecordMembers_AreValueBased()
    {
        var original = new EditUserResponse { UserId = 2, Username = "m.alvarez", UpdatedAt = DateTimeOffset.UnixEpoch };
        var copy = original with { };
        var different = original with { UserId = 3 };

        copy.Should().Be(original);
        copy.GetHashCode().Should().Be(original.GetHashCode());
        different.Should().NotBe(original);
        original.ToString().Should().Contain(nameof(EditUserResponse.UserId));
    }

    [Fact]
    public void Test_UserInsertResult_RecordMembers_AreValueBased()
    {
        var created = new UserInsertResult { Outcome = UserInsertOutcome.Created, UserId = 5 };
        var copy = created with { };
        var conflict = new UserInsertResult { Outcome = UserInsertOutcome.UsernameAlreadyExists };

        copy.Should().Be(created);
        copy.GetHashCode().Should().Be(created.GetHashCode());
        conflict.Should().NotBe(created);
        conflict.UserId.Should().BeNull();
        created.ToString().Should().Contain(nameof(UserInsertResult.Outcome));
    }

    [Fact]
    public void Test_UserCreateResult_RecordMembers_AreValueBased()
    {
        var response = new CreateUserResponse { UserId = 5, Username = "m.alvarez", CreatedAt = DateTimeOffset.UnixEpoch };
        var created = new UserCreateResult { Outcome = UserCreateOutcome.Created, Response = response };
        var copy = created with { };
        var validationFailed = new UserCreateResult { Outcome = UserCreateOutcome.ValidationFailed };

        copy.Should().Be(created);
        copy.GetHashCode().Should().Be(created.GetHashCode());
        validationFailed.Should().NotBe(created);
        validationFailed.Response.Should().BeNull();
        created.ToString().Should().Contain(nameof(UserCreateResult.Outcome));
    }

    [Fact]
    public void Test_UserEditResult_RecordMembers_AreValueBased()
    {
        var response = new EditUserResponse { UserId = 5, Username = "m.alvarez", UpdatedAt = DateTimeOffset.UnixEpoch };
        var updated = new UserEditResult { Outcome = UserEditOutcome.Updated, Response = response };
        var copy = updated with { };
        var notFound = new UserEditResult { Outcome = UserEditOutcome.UserNotFound };

        copy.Should().Be(updated);
        copy.GetHashCode().Should().Be(updated.GetHashCode());
        notFound.Should().NotBe(updated);
        notFound.Response.Should().BeNull();
        updated.ToString().Should().Contain(nameof(UserEditResult.Outcome));
    }

    [Fact]
    public void Test_UserSetActiveResult_RecordMembers_AreValueBased()
    {
        var updated = new UserSetActiveResult { Outcome = UserSetActiveOutcome.Updated };
        var copy = updated with { };
        var notFound = new UserSetActiveResult { Outcome = UserSetActiveOutcome.UserNotFound };

        copy.Should().Be(updated);
        copy.GetHashCode().Should().Be(updated.GetHashCode());
        notFound.Should().NotBe(updated);
        updated.ToString().Should().Contain(nameof(UserSetActiveResult.Outcome));
    }
}
