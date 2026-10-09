using System.Reflection;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Cross-cutting tests spanning all three CAS-007 §6 action processors
/// (<see cref="SyncCopyTaskProcessor"/>, <see cref="SyncUpdateTaskProcessor"/>,
/// <see cref="SyncPurgeTaskProcessor"/>):
/// <list type="bullet">
/// <item>SC-12 — none of the three ever depends on <c>IUserB2KeyAuthorizer</c>, only the
/// system-level <see cref="IB2Client"/> (CAS-002 §6.5's "system key, not the submitting user's
/// key" model, §12).</item>
/// <item>SC-10 (completion half) — each processor writes its per-file completion audit row
/// (<c>SyncCopyToB2</c>/<c>SyncUpdateInB2</c>/<c>SyncPurgeFromB2</c>) before the task is marked
/// <c>Completed</c> (the submission half of SC-10 — <c>*Submitted</c> before <c>202</c> — is
/// proven in <c>Cachet.ArchiveSync.Api.Tests.SyncActionServiceTests</c>).</item>
/// </list>
/// </summary>
public sealed class SyncActionProcessorSecurityTests
{
    // ---- SC-12: Copy/Update/Purge processors never reference IUserB2KeyAuthorizer -------------------

    [Theory]
    [InlineData(typeof(SyncCopyTaskProcessor))]
    [InlineData(typeof(SyncUpdateTaskProcessor))]
    [InlineData(typeof(SyncPurgeTaskProcessor))]
    public void Test_SC12_ActionProcessorsUseSystemKeyNotUserKey(Type processorType)
    {
        // Assert — the constructor accepts IB2Client (the system-level client) and never accepts
        // IUserB2KeyAuthorizer (login-validation-only, CAS-002) anywhere in its dependency list.
        var constructor = processorType.GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().Contain(typeof(IB2Client));
        parameterTypes.Should().NotContain(typeof(IUserB2KeyAuthorizer));

        // Also confirm no field of the constructed instance is ever populated from that interface —
        // walk every private field for a matching type, defensively, in case of a future refactor
        // that stops taking it via constructor injection.
        var allFieldTypes = processorType
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType);
        allFieldTypes.Should().NotContain(typeof(IUserB2KeyAuthorizer));
    }

    // ---- SC-10 (completion half): per-file completion audit before the task reaches Completed -------

    [Fact]
    public async Task Test_SC10_AllThreeActionsAuditBeforeSubmitAndBeforeCompletion()
    {
        await AssertCopyAuditsBeforeCompletionAsync();
        await AssertUpdateAuditsBeforeCompletionAsync();
        await AssertPurgeAuditsBeforeCompletionAsync();
    }

    private static async Task AssertCopyAuditsBeforeCompletionAsync()
    {
        var syncResultRepository = Substitute.For<ISyncResultRepository>();
        var fileIndexRepository = Substitute.For<IFileIndexRepository>();
        var taskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var b2Client = Substitute.For<IB2Client>();
        var odriveFileReader = Substitute.For<IODriveFileReader>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var logger = Substitute.For<ILogger<SyncCopyTaskProcessor>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = "cachet-archive-prod" })
            .Build();
        var callOrder = new List<string>();

        var row = new SyncResultRow
        {
            SyncResultId = 100601,
            TaskQueueId = 100301,
            ComparisonType = "New",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = "new-invoice.xlsx",
            OdriveRelativePath = "finance/2026/new-invoice.xlsx"
        };
        var task = new ClaimedTaskRow
        {
            TaskQueueId = 100401,
            TaskType = "SyncCopy",
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = new[] { 100601 } })
        };

        syncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(row);
        odriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), row.OdriveRelativePath!, Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        b2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>())
            .Returns(new B2UploadUrlInfo { BucketId = "b", UploadUrl = "https://upload-fake/x", AuthorizationToken = "tok" });
        b2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), row.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f", FileName = row.OdriveRelativePath!, ContentSha1 = "s", ContentLength = 1, Action = "upload", UploadTimestamp = 1 });
        auditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1).AndDoes(_ => callOrder.Add("audit-SyncCopyToB2"));
        taskQueueRepository.UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => callOrder.Add("task-completed"));

        var sut = new SyncCopyTaskProcessor(syncResultRepository, fileIndexRepository, taskQueueRepository, new TaskProgressReporterFactory(taskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), b2Client, odriveFileReader, auditLogService, configuration, logger);
        await sut.ProcessAsync(task, CancellationToken.None);

        await auditLogService.Received(1).WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncCopyToB2), Arg.Any<CancellationToken>());
        callOrder.Should().Equal("audit-SyncCopyToB2", "task-completed");
    }

    private static async Task AssertUpdateAuditsBeforeCompletionAsync()
    {
        var syncResultRepository = Substitute.For<ISyncResultRepository>();
        var fileIndexRepository = Substitute.For<IFileIndexRepository>();
        var taskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var b2Client = Substitute.For<IB2Client>();
        var odriveFileReader = Substitute.For<IODriveFileReader>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var logger = Substitute.For<ILogger<SyncUpdateTaskProcessor>>();
        var callOrder = new List<string>();

        var row = new SyncResultRow
        {
            SyncResultId = 100603,
            TaskQueueId = 100301,
            ComparisonType = "Modified",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            Sha1Changed = true,
            OdriveFileName = "policy.docx",
            OdriveRelativePath = "hr/policy.docx",
            OdriveModifiedAt = DateTimeOffset.UtcNow,
            B2FileIndexId = 55,
            B2FileName = "policy.docx",
            B2RelativePath = "hr/policy.docx"
        };
        var task = new ClaimedTaskRow
        {
            TaskQueueId = 100402,
            TaskType = "SyncUpdate",
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = new[] { 100603 } })
        };

        syncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(row);
        odriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), row.OdriveRelativePath!, Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        b2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>())
            .Returns(new B2UploadUrlInfo { BucketId = "b", UploadUrl = "https://upload-fake/x", AuthorizationToken = "tok" });
        b2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), row.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f", FileName = row.B2RelativePath!, ContentSha1 = "s", ContentLength = 1, Action = "upload", UploadTimestamp = 1 });
        auditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1).AndDoes(_ => callOrder.Add("audit-SyncUpdateInB2"));
        taskQueueRepository.UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => callOrder.Add("task-completed"));

        var sut = new SyncUpdateTaskProcessor(syncResultRepository, fileIndexRepository, taskQueueRepository, new TaskProgressReporterFactory(taskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), b2Client, odriveFileReader, auditLogService, logger);
        await sut.ProcessAsync(task, CancellationToken.None);

        await auditLogService.Received(1).WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncUpdateInB2), Arg.Any<CancellationToken>());
        callOrder.Should().Equal("audit-SyncUpdateInB2", "task-completed");
    }

    private static async Task AssertPurgeAuditsBeforeCompletionAsync()
    {
        var syncResultRepository = Substitute.For<ISyncResultRepository>();
        var fileIndexRepository = Substitute.For<IFileIndexRepository>();
        var taskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var b2Client = Substitute.For<IB2Client>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var logger = Substitute.For<ILogger<SyncPurgeTaskProcessor>>();
        var callOrder = new List<string>();

        var row = new SyncResultRow
        {
            SyncResultId = 100602,
            TaskQueueId = 100301,
            ComparisonType = "Removed",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            B2FileIndexId = 77,
            B2FileName = "old-contract.pdf",
            B2RelativePath = "contracts/2015/old-contract.pdf"
        };
        var task = new ClaimedTaskRow
        {
            TaskQueueId = 100403,
            TaskType = "SyncPurge",
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = new[] { 100602 } })
        };

        syncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(row);
        b2Client.ListFileVersionsAsync(row.B2RelativePath, row.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult
            {
                Files = new[] { new B2FileInfo { FileId = "v1", FileName = row.B2RelativePath!, ContentSha1 = "s", ContentLength = 1, Action = "upload", UploadTimestamp = 1 } }
            });
        auditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1).AndDoes(_ => callOrder.Add("audit-SyncPurgeFromB2"));
        taskQueueRepository.UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask).AndDoes(_ => callOrder.Add("task-completed"));

        // CAS-062: no local mirror copy in this scenario.
        var b2MirrorFileRemover = Substitute.For<IB2MirrorFileRemover>();
        b2MirrorFileRemover.RemoveAsync(default!, default).ReturnsForAnyArgs(B2MirrorRemoveResult.NotPresent);
        var sut = new SyncPurgeTaskProcessor(syncResultRepository, fileIndexRepository, taskQueueRepository, new TaskProgressReporterFactory(taskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), b2Client, b2MirrorFileRemover, auditLogService, logger);
        await sut.ProcessAsync(task, CancellationToken.None);

        await auditLogService.Received(1).WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncPurgeFromB2), Arg.Any<CancellationToken>());
        callOrder.Should().Equal("audit-SyncPurgeFromB2", "task-completed");
    }
}
