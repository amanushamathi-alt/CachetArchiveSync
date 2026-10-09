using System.Reflection;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="QuarantineRemoveService"/> (CAS-014 §2.2/§8/§13 — "Remove Item",
/// BRD §12.3 items 3, 7). Covers V1, V2, V4, V5, SC-2, SC-3 per the spec's §13 Test Plan
/// "QuarantineRemoveServiceTests" assignment.
/// </summary>
public sealed class QuarantineRemoveServiceTests
{
    private sealed class Fixture
    {
        public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<QuarantineRemoveService> Logger { get; } = Substitute.For<ILogger<QuarantineRemoveService>>();

        public QuarantineRemoveService CreateSut() =>
            new(QuarantineEntryRepository, AuditLogService, Logger);
    }

    private static QuarantineEntryForDeleteRow ActiveEntry(
        int quarantineEntryId, int fileIndexId, string path, string name) => new()
        {
            QuarantineEntryId = quarantineEntryId,
            FileIndexId = fileIndexId,
            Name = name,
            Path = path
        };

    // ---- V1: fileIds must be non-empty --------------------------------------------------------------

    [Fact]
    public async Task Test_V1_RemoveEmptyFileIdsRejected()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = Array.Empty<int>() };

        // Act
        var result = await sut.RemoveAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — rejected before any resolution/delete attempt.
        result.Outcome.Should().Be(QuarantineRemoveOutcome.ValidationFailed);
        await fixture.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().HardDeleteActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- V2: each fileId must resolve to an Active quarantine_entry row, reported per-file ----------

    [Fact]
    public async Task Test_V2_RemoveUnknownOrNonActiveEntryReportedPerFile()
    {
        // Arrange — 100801 resolves and succeeds fully; 999999 does not resolve to any Active row.
        var fixture = new Fixture();
        var goodEntry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(goodEntry);
        fixture.QuarantineEntryRepository.HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>()).Returns(true);
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(999999, Arg.Any<CancellationToken>())
            .Returns((QuarantineEntryForDeleteRow?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 100801, 999999 } };

        // Act
        var result = await sut.RemoveAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — a per-file Failed/file-not-found result for the unresolvable id, alongside the
        // other file's independent success, in the same 200-shaped response (not a whole-batch 404).
        result.Outcome.Should().Be(QuarantineRemoveOutcome.Completed);
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");
        result.Response.Results.Should().ContainSingle(r =>
            r.FileId == 999999 && r.Result == "Failed" && r.Reason == "file-not-found");
        await fixture.QuarantineEntryRepository.DidNotReceive().HardDeleteActiveAsync(999999, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V2_AllFilesUnresolvableReturnsWholeRequestNotFound()
    {
        // Arrange — every requested fileId fails to resolve -> whole-request AllFilesNotFound.
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(999999, Arg.Any<CancellationToken>())
            .Returns((QuarantineEntryForDeleteRow?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 999999 } };

        // Act
        var result = await sut.RemoveAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineRemoveOutcome.AllFilesNotFound);
    }

    // ---- V4/SC-1: audit is written before the file's result is appended, in the required order ------

    [Fact]
    public async Task Test_V4_RemoveAuditsBeforeResponse()
    {
        // Arrange
        var fixture = new Fixture();
        var callOrder = new List<string>();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.QuarantineEntryRepository
            .HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>())
            .Returns(true)
            .AndDoes(_ => callOrder.Add("hard-delete"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 100801 } };

        // Act
        var result = await sut.RemoveAsync(request, actingUserId: 9, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert
        result.Outcome.Should().Be(QuarantineRemoveOutcome.Completed);
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineRemoved &&
                e.TabModule == "Quarantine" &&
                e.FilePath == entry.Path &&
                e.UserId == 9 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());

        callOrder.Should().Equal("hard-delete", "audit", "response-returned");
    }

    // ---- V5: no B2 call and no file_index mutation, ever — no such dependency even exists ------------

    [Fact]
    public void Test_V5_RemoveNeverCallsB2OrFileIndex()
    {
        // Assert — the constructor itself proves this: QuarantineRemoveService takes only
        // IQuarantineEntryRepository, IAuditLogService, and ILogger<QuarantineRemoveService> as
        // dependencies. Unlike QuarantinePermanentDeleteService, there is no IB2Client or
        // IFileIndexRepository parameter for the service to invoke in the first place — expressed
        // here as a reflection-based assertion over the constructor's parameter types so a future
        // change that reintroduces either dependency fails this test.
        var constructor = typeof(QuarantineRemoveService).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(typeof(IB2Client));
        parameterTypes.Should().NotContain(typeof(IFileIndexRepository));
        parameterTypes.Should().BeEquivalentTo(new[]
        {
            typeof(IQuarantineEntryRepository),
            typeof(IAuditLogService),
            typeof(ILogger<QuarantineRemoveService>)
        });
    }

    // ---- SC-2: a repeat remove of an already-removed entry fails cleanly, never a second success -----

    [Fact]
    public async Task Test_SC2_RepeatRemoveOfAlreadyRemovedEntryFailsCleanly()
    {
        // Arrange — 100801 was already hard-deleted by an earlier call, so GetActiveForDeleteAsync
        // now resolves it to null; 100802 is a distinct, still-Active file in the same batch to
        // prove this is a per-file outcome, not a whole-batch 404.
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>())
            .Returns((QuarantineEntryForDeleteRow?)null);
        var stillActiveEntry = ActiveEntry(100802, fileIndexId: 200802, path: "contracts/2020/invoice-009.pdf", name: "invoice-009.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100802, Arg.Any<CancellationToken>()).Returns(stillActiveEntry);
        fixture.QuarantineEntryRepository.HardDeleteActiveAsync(100802, Arg.Any<CancellationToken>()).Returns(true);
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 100801, 100802 } };

        // Act
        var result = await sut.RemoveAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — no throw; the already-removed entry reports Failed/file-not-found, not a second
        // Success; no hard-delete attempt for it.
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "file-not-found");
        result.Response.Results.Should().ContainSingle(r => r.FileId == 100802 && r.Result == "Success");
        await fixture.QuarantineEntryRepository.DidNotReceive().HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC2_ConcurrentHardDeleteRaceReportedAsFileNotFound()
    {
        // Arrange — the row resolves as Active, but a concurrent request already removed it by the
        // time HardDeleteActiveAsync runs (rows_affected == 0 -> false).
        var fixture = new Fixture();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.QuarantineEntryRepository.HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>()).Returns(false);
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 100801 } };

        // Act
        var result = await sut.RemoveAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — reported per-file Failed/file-not-found; never throws; no audit written for it.
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "file-not-found");
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-3: one file resolves and is removed, the other does not, both in the same response -------

    [Fact]
    public async Task Test_SC3_PartialBatchIsolatedPerFile()
    {
        // Arrange — 100801 resolves and is removed+audited; 999999 never resolves.
        var fixture = new Fixture();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.QuarantineEntryRepository.HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>()).Returns(true);
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(999999, Arg.Any<CancellationToken>())
            .Returns((QuarantineEntryForDeleteRow?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineRemoveRequest { FileIds = new[] { 100801, 999999 } };

        // Act
        var result = await sut.RemoveAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — both outcomes present in the same 200 response.
        result.Outcome.Should().Be(QuarantineRemoveOutcome.Completed);
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");
        result.Response.Results.Should().ContainSingle(r =>
            r.FileId == 999999 && r.Result == "Failed" && r.Reason == "file-not-found");

        await fixture.QuarantineEntryRepository.Received(1).HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineRemoved && e.FilePath == entry.Path),
            Arg.Any<CancellationToken>());
    }
}
