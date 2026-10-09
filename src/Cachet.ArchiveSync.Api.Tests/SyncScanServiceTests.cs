using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-045 §13 — <see cref="SyncScanService.GetResultsAsync"/>'s <c>excludeSynced</c> threading
/// (V10, SC-13 default-<c>false</c> parity). The stored procedure's own filtering of <c>Synced</c> rows
/// is a database concern (see <c>dbo.sync_result_lst</c>); here the service is proven to hand the flag,
/// unchanged, to every one of the three per-panel repository reads and to surface the repository's
/// (already filtered) rows and totals in its response.
/// </summary>
public sealed class SyncScanServiceTests
{
    private const int ScanTaskId = 100003;

    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public ISyncResultRepository SyncResultRepository { get; } = Substitute.For<ISyncResultRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<SyncScanService> Logger { get; } = Substitute.For<ILogger<SyncScanService>>();

        public Fixture()
        {
            TaskQueueRepository.GetAsync(ScanTaskId, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
            {
                TaskQueueId = ScanTaskId,
                TaskType = "ContentScan",
                Status = "Completed",
                SubmittedByUserId = 1,
                SubmittedAt = DateTimeOffset.UtcNow
            });
        }

        public SyncScanService CreateSut() =>
            new(TaskQueueRepository, SyncResultRepository, AuditLogService, Logger);

        public void SeedRepository(string comparisonType, bool excludeSynced, IReadOnlyList<SyncResultRow> rows, int total) =>
            SyncResultRepository
                .ListAsync(ScanTaskId, comparisonType, Arg.Any<int>(), Arg.Any<int>(), excludeSynced, Arg.Any<CancellationToken>())
                .Returns((rows, total));
    }

    private static SyncResultRow Row(int id, string comparisonType, string status) => new()
    {
        SyncResultId = id,
        TaskQueueId = ScanTaskId,
        ComparisonType = comparisonType,
        SyncStatus = status,
        CreatedAt = DateTimeOffset.UtcNow,
        OdriveFileName = $"file-{id}.pdf",
        OdriveRelativePath = $"dir/file-{id}.pdf",
        OdriveModifiedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Test_V10_ExcludeSyncedFiltersRowsAndTotal()
    {
        // Arrange — with excludeSynced=true the repository (stored procedure) returns only the
        // non-Synced rows and a TotalCount counting only those; the service must pass the flag on for
        // all three panels and surface exactly that.
        var fixture = new Fixture();
        fixture.SeedRepository("Removed", excludeSynced: true, new[] { Row(1, "Removed", "Pending"), Row(2, "Removed", "Failed") }, total: 2);
        fixture.SeedRepository("New", excludeSynced: true, new[] { Row(3, "New", "Pending") }, total: 1);
        fixture.SeedRepository("Modified", excludeSynced: true, Array.Empty<SyncResultRow>(), total: 0);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(ScanTaskId, 2, 25, 3, 100, 4, 50, excludeSynced: true, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncResultsOutcome.Found);
        result.Response!.RemovedFromODrive!.TotalCount.Should().Be(2);
        result.Response.RemovedFromODrive.Items.Select(i => i.SyncStatus).Should().BeEquivalentTo(new[] { "Pending", "Failed" });
        result.Response.NewOnODrive!.TotalCount.Should().Be(1);
        result.Response.ModifiedOnODrive!.TotalCount.Should().Be(0);

        await fixture.SyncResultRepository.Received(1).ListAsync(ScanTaskId, "New", 2, 25, true, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).ListAsync(ScanTaskId, "Removed", 3, 100, true, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).ListAsync(ScanTaskId, "Modified", 4, 50, true, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V10_ExcludeSyncedDefaultFalseKeepsSyncedRowsAndTotals()
    {
        // Arrange — default-false parity (SC-13): Synced rows are still returned and counted.
        var fixture = new Fixture();
        fixture.SeedRepository("Removed", excludeSynced: false, new[] { Row(1, "Removed", "Pending"), Row(2, "Removed", "Synced") }, total: 2);
        fixture.SeedRepository("New", excludeSynced: false, Array.Empty<SyncResultRow>(), total: 0);
        fixture.SeedRepository("Modified", excludeSynced: false, Array.Empty<SyncResultRow>(), total: 0);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetResultsAsync(ScanTaskId, 1, 50, 1, 50, 1, 50, excludeSynced: false, CancellationToken.None);

        // Assert
        result.Response!.RemovedFromODrive!.Items.Select(i => i.SyncStatus).Should().Contain("Synced");
        result.Response.RemovedFromODrive.TotalCount.Should().Be(2);
        await fixture.SyncResultRepository.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }
}
