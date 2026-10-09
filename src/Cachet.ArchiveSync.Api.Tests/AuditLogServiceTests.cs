using System.Reflection;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="AuditLogService"/> (CAS-005). Proves everything provable at the
/// service layer with <see cref="IAuditLogRepository"/> mocked — SC-3, SC-4, V-1, V-2, V-3.
/// SC-1/SC-2 require a real <c>dbo.audit_log</c> round-trip and live in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.AuditLogRepositoryTests</c> (Skip-marked in this
/// sandbox — no live SQL Server instance available).
/// </summary>
public sealed class AuditLogServiceTests
{
    private const string ValidTabModule = "Auth";

    private static AuditLogEntry ValidEntry(
        string? filePath = null,
        int? taskQueueId = null,
        string? resultDetail = null,
        string tabModule = ValidTabModule) =>
        new()
        {
            CreatedBy = "api-svc",
            UpdatedBy = "api-svc",
            UserId = 42,
            TabModule = tabModule,
            Action = AuditAction.Login,
            FilePath = filePath,
            TaskQueueId = taskQueueId,
            Result = AuditResult.Success,
            ResultDetail = resultDetail
        };

    private static (AuditLogService Service, IAuditLogRepository Repository) CreateSut()
    {
        var repository = Substitute.For<IAuditLogRepository>();
        var logger = Substitute.For<ILogger<AuditLogService>>();
        var service = new AuditLogService(repository, logger);
        return (service, repository);
    }

    // ---- V1 / SC-3: invalid tab_module ------------------------------------------------------

    [Theory]
    [InlineData("NotARealModule")]
    [InlineData("")]
    [InlineData("auth")] // wrong case — CK_audit_log_tab_module values are exact-string matches
    public async Task Test_V1_InvalidTabModuleRejected(string invalidTabModule)
    {
        // Arrange
        var (service, _) = CreateSut();
        var entry = ValidEntry(tabModule: invalidTabModule);

        // Act
        var act = async () => await service.WriteAsync(entry, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Test_SC3_InvalidTabModuleFailsFast()
    {
        // Arrange
        var (service, repository) = CreateSut();
        var entry = ValidEntry(tabModule: "NotARealModule");

        // Act
        var act = async () => await service.WriteAsync(entry, CancellationToken.None);

        // Assert — rejected, and no database round-trip was ever attempted
        await act.Should().ThrowAsync<ArgumentException>();
        await repository.DidNotReceive().InsertAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- V2: enum-typed parameters (compile-time guarantee, confirmed by reflection) --------

    [Fact]
    public void Test_V2_ServiceSignatureUsesEnumTypes()
    {
        // Arrange
        var writeAsync = typeof(IAuditLogService).GetMethod(nameof(IAuditLogService.WriteAsync));
        var entryParameterType = writeAsync!.GetParameters()[0].ParameterType;

        var actionProperty = entryParameterType.GetProperty(nameof(AuditLogEntry.Action));
        var resultProperty = entryParameterType.GetProperty(nameof(AuditLogEntry.Result));

        // Assert — the C# type system, not runtime validation, enforces V2
        entryParameterType.Should().Be(typeof(AuditLogEntry));
        actionProperty!.PropertyType.Should().Be(typeof(AuditAction));
        actionProperty.PropertyType.IsEnum.Should().BeTrue();
        resultProperty!.PropertyType.Should().Be(typeof(AuditResult));
        resultProperty.PropertyType.IsEnum.Should().BeTrue();
    }

    // ---- V3: optional fields accept NULL -----------------------------------------------------

    [Fact]
    public async Task Test_V3_OptionalFieldsAcceptNull()
    {
        // Arrange
        var (service, repository) = CreateSut();
        const int expectedAuditLogId = 100123;
        repository
            .InsertAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(expectedAuditLogId);

        var entry = ValidEntry(filePath: null, taskQueueId: null, resultDetail: null);

        // Act
        var act = async () => await service.WriteAsync(entry, CancellationToken.None);

        // Assert — no exception, and the nulls were passed through unchanged to the repository
        (await act.Should().NotThrowAsync()).Which.Should().Be(expectedAuditLogId);
        await repository.Received(1).InsertAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.FilePath == null &&
                e.TaskQueueId == null &&
                e.ResultDetail == null),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-4: no sensitive data leaked into exceptions --------------------------------------

    [Fact]
    public async Task Test_SC4_NoSensitiveDataInExceptionMessages()
    {
        // Arrange
        var (service, repository) = CreateSut();
        const string repositoryFailureMessage = "Timeout expired while inserting audit row.";
        repository
            .InsertAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(repositoryFailureMessage));

        var entry = ValidEntry(resultDetail: "some non-secret detail");

        // Act
        var act = async () => await service.WriteAsync(entry, CancellationToken.None);

        // Assert — the service rethrows the repository's exception unchanged: it never wraps or
        // enriches it with entry fields (which is what would risk leaking data beyond the audit
        // row itself), and it never contains B2-key/session-token-shaped material.
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be(repositoryFailureMessage);
        thrown.Which.Message.Should().NotContain("applicationKey");
        thrown.Which.Message.Should().NotContain("sessionToken");
        thrown.Which.Message.Should().NotContain("password");
    }
}
