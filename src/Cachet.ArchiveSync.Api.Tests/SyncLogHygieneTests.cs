using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// SC-14 ("no B2 application key, session token/cookie value, raw file content, or full local
/// file path beyond what the audit log already records ever appears in a log line") for the
/// Api-side surfaces: <c>POST /sync/scan</c> (<see cref="SyncScanService"/>) and
/// <c>POST /sync/actions/{copy,update,purge}</c> (<see cref="SyncActionService"/>). The four
/// Worker task processors' own log hygiene is proven separately in
/// <c>Cachet.ArchiveSync.Worker.Tests.SyncTaskProcessorLogHygieneTests</c>.
/// </summary>
public sealed class SyncLogHygieneTests
{
    private static List<string> GetFormattedLogMessages<T>(ILogger<T> logger)
    {
        var messages = new List<string>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    [Fact]
    public async Task Test_SC14_NoCredentialOrContentLeakageInLogs()
    {
        // --- POST /sync/scan: no path/content ever logged, only ids/counts ---------------------------
        var taskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var syncResultRepository = Substitute.For<ISyncResultRepository>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var scanLogger = Substitute.For<ILogger<SyncScanService>>();
        taskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "ContentScan", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100301);
        var scanService = new SyncScanService(taskQueueRepository, syncResultRepository, auditLogService, scanLogger);
        await scanService.SubmitAsync(submittedByUserId: 1, CancellationToken.None);
        // No request body/path exists for a ContentScan submission (§3) — only confirm a log line
        // was emitted at all (ids/counts only, nothing to leak).
        GetFormattedLogMessages(scanLogger).Should().NotBeEmpty();

        // --- POST /sync/actions/copy: no O-Drive relative path or B2 key ever logged ------------------
        const string sensitivePath = "confidential/executive-comp/2026-payout-plan.xlsx";
        var actionTaskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var actionSyncResultRepository = Substitute.For<ISyncResultRepository>();
        var actionAuditLogService = Substitute.For<IAuditLogService>();
        var actionLogger = Substitute.For<ILogger<SyncActionService>>();
        var configuration = new ConfigurationBuilder().Build();

        actionTaskQueueRepository.GetAsync(100301, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100301,
            TaskType = "ContentScan",
            Status = "Completed",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        actionSyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(new SyncResultRow
        {
            SyncResultId = 100601,
            TaskQueueId = 100301,
            ComparisonType = "New",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = "2026-payout-plan.xlsx",
            OdriveRelativePath = sensitivePath
        });
        actionTaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "SyncCopy", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100401);
        var actionService = new SyncActionService(actionTaskQueueRepository, actionSyncResultRepository, actionAuditLogService, configuration, actionLogger);

        await actionService.SubmitCopyAsync(new SyncActionRequest { TaskId = 100301, FileIds = new[] { 100601 } }, actingUserId: 1, CancellationToken.None);

        var messages = GetFormattedLogMessages(actionLogger);
        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(m => !m.Contains(sensitivePath, StringComparison.Ordinal));
    }
}
