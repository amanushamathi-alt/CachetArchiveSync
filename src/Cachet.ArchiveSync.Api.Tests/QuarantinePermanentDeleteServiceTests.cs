using System.Reflection;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="QuarantinePermanentDeleteService"/> (CAS-008 §6.4, FR-16/FR-17, and
/// CAS-018 §2's live per-user B2 authorization pre-batch check). Covers the spec's §13 Test Plan
/// "QuarantinePermanentDeleteServiceTests" assignment: pre-existing V1-V6/SC-3/SC-4/SC-5/SC-8
/// (CAS-008), plus CAS-018's V7, V8, SC-1 through SC-7.
///
/// CAS-018 changed this service's constructor to remove <see cref="IB2Client"/> entirely in favor
/// of <see cref="IUserRepository"/>/<see cref="ICredentialProtector"/>/<see cref="IUserB2KeyAuthorizer"/>/
/// <see cref="IUserScopedB2DeleteClient"/> (CAS-018 §5) — every fixture and mock below has been
/// updated to the new constructor shape and the new per-file B2 call surface accordingly.
/// </summary>
public sealed class QuarantinePermanentDeleteServiceTests
{
    private const string PlainKeyId = "plain-key-id-0000000000001";
    private const string PlainApplicationKey = "K001PlainApplicationKeyValueNeverLog";
    private const string CipherKeyId = "ct:keyid";
    private const string CipherApplicationKey = "ct:key";
    private const string BucketId = "bucket-1";
    private const string LiveAuthToken = "4_liveUserAuthToken_neverLog0000000000000000000000000000000000";

    private sealed class Fixture
    {
        public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
        public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
        public IUserB2KeyAuthorizer UserB2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
        public IUserScopedB2DeleteClient UserScopedB2DeleteClient { get; } = Substitute.For<IUserScopedB2DeleteClient>();
        public IB2MirrorFileRemover B2MirrorFileRemover { get; } = Substitute.For<IB2MirrorFileRemover>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<QuarantinePermanentDeleteService> Logger { get; } = Substitute.For<ILogger<QuarantinePermanentDeleteService>>();

        public Fixture()
        {
            // CAS-062 default: no local mirror copy.
            B2MirrorFileRemover.RemoveAsync(default!, default).ReturnsForAnyArgs(B2MirrorRemoveResult.NotPresent);
        }

        public QuarantinePermanentDeleteService CreateSut() =>
            new(QuarantineEntryRepository, FileIndexRepository, UserRepository, CredentialProtector,
                UserB2KeyAuthorizer, UserScopedB2DeleteClient, B2MirrorFileRemover, AuditLogService, Logger);
    }

    private static UserAccount ActingUser(int userId) => new()
    {
        UserId = userId,
        Username = "jdoe",
        PasswordHash = "irrelevant-hash",
        B2ApplicationKeyId = CipherKeyId,
        B2ApplicationKey = CipherApplicationKey,
        IsActive = true,
        SessionTokenVersion = 0
    };

    private static B2AuthorizationInfo LiveAuthorization(params string[] capabilities) => new()
    {
        AccountId = "acct-1",
        AuthorizationToken = LiveAuthToken,
        ApiUrl = "https://api-fake.backblazeb2.com",
        DownloadUrl = "https://download-fake.backblazeb2.com",
        Capabilities = capabilities,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
    };

    /// <summary>CAS-018 §2 steps 1-2: wires the live pre-batch check to succeed with
    /// <c>deleteFiles</c> in its capability set.</summary>
    private static void ArrangeLiveAuthorizationSucceeds(Fixture fixture, int actingUserId)
    {
        fixture.UserRepository.GetByIdAsync(actingUserId, Arg.Any<CancellationToken>())
            .Returns(ActingUser(actingUserId));
        fixture.CredentialProtector.Unprotect(CipherKeyId).Returns(PlainKeyId);
        fixture.CredentialProtector.Unprotect(CipherApplicationKey).Returns(PlainApplicationKey);
        fixture.UserB2KeyAuthorizer.AuthorizeAsync(PlainKeyId, PlainApplicationKey, Arg.Any<CancellationToken>())
            .Returns(LiveAuthorization("listFiles", "readFiles", "writeFiles", "deleteFiles"));
        fixture.UserScopedB2DeleteClient.GetBucketIdAsync(Arg.Any<CancellationToken>()).Returns(BucketId);
    }

    private static QuarantineEntryForDeleteRow ActiveEntry(
        int quarantineEntryId, int fileIndexId, string path, string name) => new()
        {
            QuarantineEntryId = quarantineEntryId,
            FileIndexId = fileIndexId,
            Name = name,
            Path = path
        };

    private static B2ListFileVersionsResult SinglePage(string path, params string[] b2FileIds) => new()
    {
        Files = b2FileIds.Select(id => new B2FileInfo
        {
            FileId = id,
            FileName = path,
            ContentSha1 = "fake-sha1",
            ContentLength = 1024,
            Action = "upload",
            UploadTimestamp = 1
        }).ToList(),
        NextFileName = null,
        NextFileId = null
    };

    /// <summary>Wires the happy path for one <paramref name="entry"/>: live authorization
    /// succeeds, resolves, one B2 version deletes cleanly, local soft-delete succeeds, status
    /// transition succeeds.</summary>
    private static void ArrangeFullSuccess(Fixture fixture, int actingUserId, QuarantineEntryForDeleteRow entry, string b2FileId = "4_z_v1")
    {
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId);
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(entry.QuarantineEntryId, Arg.Any<CancellationToken>())
            .Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(entry.Path, b2FileId));
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(entry.QuarantineEntryId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    // ---- V1: fileIds must be non-empty (checked before the live authorization check) -----------

    [Fact]
    public async Task Test_V1_DeleteEmptyFileIdsRejected()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = Array.Empty<int>(), Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineDeleteOutcome.ValidationFailed);
        await fixture.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.UserScopedB2DeleteClient.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V2: confirm must be true, checked before any other validation or deletion attempt ------

    [Fact]
    public async Task Test_V2_DeleteWithoutConfirmRejectedBeforeAnyDeletion()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = false };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — rejected before even the live authorization check runs.
        result.Outcome.Should().Be(QuarantineDeleteOutcome.ConfirmationRequired);
        await fixture.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- V3: each fileId must resolve to an Active quarantine_entry row, reported per-file -------

    [Fact]
    public async Task Test_V3_DeleteUnknownOrNonActiveEntryReportedPerFile()
    {
        // Arrange — 100801 resolves and succeeds fully; 999999 does not resolve to any Active row.
        var fixture = new Fixture();
        var goodEntry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        ArrangeFullSuccess(fixture, actingUserId: 1, goodEntry);
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(999999, Arg.Any<CancellationToken>())
            .Returns((QuarantineEntryForDeleteRow?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801, 999999 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — a per-file Failed/file-not-found result for the unresolvable id, alongside the
        // other file's independent success, in the same 200-shaped response (not a whole-batch 404).
        result.Outcome.Should().Be(QuarantineDeleteOutcome.Completed);
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");
        result.Response.Results.Should().ContainSingle(r =>
            r.FileId == 999999 && r.Result == "Failed" && r.Reason == "file-not-found");
    }

    [Fact]
    public async Task Test_V3_AllFilesUnresolvableReturnsWholeRequestNotFound()
    {
        // Arrange — every requested fileId fails to resolve -> whole-request AllFilesNotFound (§6).
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(999999, Arg.Any<CancellationToken>())
            .Returns((QuarantineEntryForDeleteRow?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 999999 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineDeleteOutcome.AllFilesNotFound);
    }

    // ---- V4: every B2 version of the object is deleted, not just the current one -----------------

    [Fact]
    public async Task Test_V4_DeletePurgesAllB2VersionsNotJustCurrent()
    {
        // Arrange — two versions of the same object name.
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(entry.Path, "4_z_v1", "4_z_v2"));
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(100801, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — both versions targeted, not just one, both using the live authorization.
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");
        await fixture.UserScopedB2DeleteClient.Received(1).DeleteFileVersionAsync(
            Arg.Any<B2AuthorizationInfo>(), entry.Path, "4_z_v1", Arg.Any<CancellationToken>());
        await fixture.UserScopedB2DeleteClient.Received(1).DeleteFileVersionAsync(
            Arg.Any<B2AuthorizationInfo>(), entry.Path, "4_z_v2", Arg.Any<CancellationToken>());
    }

    // ---- V5: B2 delete failure leaves local state unchanged, audits DeleteFailedB2 ----------------

    [Fact]
    public async Task Test_V5_B2DeleteFailureLeavesLocalStateUnchangedAndAudits()
    {
        // Arrange
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "B2 call failed."));
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — per-file Failed result; local file_index/quarantine_entry never touched.
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "b2-delete-failed");
        await fixture.FileIndexRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.DeleteFailedB2 &&
                e.TabModule == "Quarantine" &&
                e.FilePath == entry.Path &&
                e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    // ---- V6: local soft-delete failure after a successful B2 delete audits DeleteFailedLocal -----

    [Fact]
    public async Task Test_V6_LocalFailureAfterB2SuccessAuditsDeleteFailedLocal()
    {
        // Arrange — B2 delete succeeds, then the local file_index soft-delete throws.
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(entry.Path, "4_z_v1"));
        fixture.FileIndexRepository.UpdateAsync(
                200801, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("local update failed"));
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — B2 delete already happened (no undo/resurrection, FR-17); audited as DeleteFailedLocal.
        await fixture.UserScopedB2DeleteClient.Received(1).DeleteFileVersionAsync(
            Arg.Any<B2AuthorizationInfo>(), entry.Path, "4_z_v1", Arg.Any<CancellationToken>());
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "local-delete-failed");
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.DeleteFailedLocal &&
                e.TabModule == "Quarantine" &&
                e.FilePath == entry.Path &&
                e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: a concurrent request already moved the row out of Active at the --------
    // ---- status-transition step (B2 and file_index are already handled regardless, FR-17) --------

    [Fact]
    public async Task Test_StatusTransitionRaceLostAfterB2AndLocalSucceed_AuditsDeleteFailedLocal()
    {
        // Arrange — B2 delete and the local soft-delete both succeed, but a concurrent request has
        // already transitioned the row out of Active by the time this call reaches that step.
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(entry.Path, "4_z_v1"));
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(100801, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — no rollback of the already-completed B2/local steps (FR-17); reported the same
        // way as a local failure, since neither undoes anything already done.
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "file-not-found");
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.DeleteFailedLocal &&
                e.TabModule == "Quarantine" &&
                e.FilePath == entry.Path &&
                e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-3 (CAS-008): full success deletes B2+local, transitions status, audits before response ---

    [Fact]
    public async Task Test_SC3_DeleteSucceedsAndAuditsBeforeResponse()
    {
        // Arrange
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 9);
        var callOrder = new List<string>();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(entry.Path, "4_z_v1"));
        fixture.UserScopedB2DeleteClient
            .When(c => c.DeleteFileVersionAsync(Arg.Any<B2AuthorizationInfo>(), entry.Path, "4_z_v1", Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("b2-delete"));
        fixture.FileIndexRepository
            .UpdateAsync(200801, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("local-soft-delete"));
        fixture.QuarantineEntryRepository
            .TryTransitionToDeletedAsync(100801, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true)
            .AndDoes(_ => callOrder.Add("status-transition"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 9, CancellationToken.None);
        callOrder.Add("response-returned");

        // Assert
        result.Outcome.Should().Be(QuarantineDeleteOutcome.Completed);
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.Deleted &&
                e.TabModule == "Quarantine" &&
                e.FilePath == entry.Path &&
                e.UserId == 9 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());

        callOrder.Should().Equal("b2-delete", "local-soft-delete", "status-transition", "audit", "response-returned");
    }

    // ---- SC-4 (CAS-008): missing confirm makes no state change whatsoever -------------------------

    [Fact]
    public async Task Test_SC4_MissingConfirmMakesNoStateChange()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801, 100802 }, Confirm = false };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — 422 confirmation-required, and absolutely no live-authorization call, B2 call,
        // local mutation, status transition, or audit write for any requested file.
        result.Outcome.Should().Be(QuarantineDeleteOutcome.ConfirmationRequired);
        await fixture.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.UserScopedB2DeleteClient.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.UserScopedB2DeleteClient.DidNotReceive().DeleteFileVersionAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-5 (CAS-008): one file's B2 failure is isolated from another file's success -------------

    [Fact]
    public async Task Test_SC5_PartialBatchFailureIsolatedPerFile()
    {
        // Arrange — 100801 fails at the B2 step; 100802 succeeds fully.
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        var failingEntry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        var succeedingEntry = ActiveEntry(100802, fileIndexId: 200802, path: "contracts/2020/invoice-009.pdf", name: "invoice-009.pdf");

        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(failingEntry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, failingEntry.Path, failingEntry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<B2ListFileVersionsResult>(_ => throw new B2ApiException(500, "internal_error", "B2 call failed."));

        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100802, Arg.Any<CancellationToken>()).Returns(succeedingEntry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, succeedingEntry.Path, succeedingEntry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(succeedingEntry.Path, "4_z_v9"));
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(100802, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801, 100802 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — both outcomes present in the same 200 response.
        result.Outcome.Should().Be(QuarantineDeleteOutcome.Completed);
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Failed");
        result.Response.Results.Should().ContainSingle(r => r.FileId == 100802 && r.Result == "Success");

        // The failing file's quarantine_entry is never transitioned; the succeeding file's is.
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            100801, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.Received(1).TryTransitionToDeletedAsync(
            100802, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.DeleteFailedB2 && e.FilePath == failingEntry.Path),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.Deleted && e.FilePath == succeedingEntry.Path),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-8 (CAS-008): a repeat delete of an already-Deleted entry fails cleanly ------------------

    [Fact]
    public async Task Test_SC8_RepeatDeleteOfAlreadyDeletedEntryFailsCleanly()
    {
        // Arrange — 100801 was already transitioned to Deleted by an earlier call, so
        // GetActiveForDeleteAsync now resolves it to null (repository applies the Active filter);
        // 100802 is a distinct, still-Active file in the same batch to prove this is a per-file
        // outcome, not a whole-batch 404.
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>())
            .Returns((QuarantineEntryForDeleteRow?)null);
        var stillActiveEntry = ActiveEntry(100802, fileIndexId: 200802, path: "contracts/2020/invoice-009.pdf", name: "invoice-009.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100802, Arg.Any<CancellationToken>()).Returns(stillActiveEntry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, stillActiveEntry.Path, stillActiveEntry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(stillActiveEntry.Path, "4_z_v1"));
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(100802, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801, 100802 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — the already-deleted entry reports Failed/file-not-found, not a second Success;
        // no B2 or local mutation is attempted for it.
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "file-not-found");
        result.Response.Results.Should().ContainSingle(r => r.FileId == 100802 && r.Result == "Success");
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            100801, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // =================================================================================================
    // CAS-018 — live per-user B2 authorization pre-batch check
    // =================================================================================================

    // ---- V7: delete requires a live b2_authorize_account check of the acting user's own key ---------

    [Fact]
    public async Task Test_V7_LiveB2AuthorizationRequiredBeforeAnyDelete()
    {
        // Arrange
        var fixture = new Fixture();
        var callOrder = new List<string>();
        fixture.UserRepository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(ActingUser(7));
        fixture.CredentialProtector.Unprotect(CipherKeyId).Returns(PlainKeyId);
        fixture.CredentialProtector.Unprotect(CipherApplicationKey).Returns(PlainApplicationKey);
        fixture.UserB2KeyAuthorizer.AuthorizeAsync(PlainKeyId, PlainApplicationKey, Arg.Any<CancellationToken>())
            .Returns(LiveAuthorization("listFiles", "readFiles", "writeFiles", "deleteFiles"))
            .AndDoes(_ => callOrder.Add("live-authorize"));
        fixture.UserScopedB2DeleteClient.GetBucketIdAsync(Arg.Any<CancellationToken>()).Returns(BucketId);
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(entry.Path, "4_z_v1"));
        fixture.UserScopedB2DeleteClient
            .When(c => c.DeleteFileVersionAsync(Arg.Any<B2AuthorizationInfo>(), entry.Path, "4_z_v1", Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("b2-delete"));
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(100801, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 7, CancellationToken.None);

        // Assert — the live check ran, using the acting user's own decrypted key (never the system
        // key — there is no IB2Client dependency at all on this service), and ran strictly before
        // any per-file B2 delete call.
        result.Outcome.Should().Be(QuarantineDeleteOutcome.Completed);
        await fixture.UserRepository.Received(1).GetByIdAsync(7, Arg.Any<CancellationToken>());
        await fixture.UserB2KeyAuthorizer.Received(1).AuthorizeAsync(PlainKeyId, PlainApplicationKey, Arg.Any<CancellationToken>());
        callOrder.Should().Equal("live-authorize", "b2-delete");
    }

    // ---- V8 / SC-2: live-authorization failure aborts the whole batch before any file is touched ----

    [Fact]
    public async Task Test_V8_AuthorizationFailureAbortsWholeBatchBeforeAnyFile()
    {
        // Arrange — the live check itself fails outright (e.g. revoked key / B2 API error).
        var fixture = new Fixture();
        fixture.UserRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ActingUser(1));
        fixture.CredentialProtector.Unprotect(CipherKeyId).Returns(PlainKeyId);
        fixture.CredentialProtector.Unprotect(CipherApplicationKey).Returns(PlainApplicationKey);
        fixture.UserB2KeyAuthorizer.AuthorizeAsync(PlainKeyId, PlainApplicationKey, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "revoked"));
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801, 100802 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — whole-request denial; zero per-file resolution/B2/local mutation for EITHER
        // requested fileId, and exactly one audit entry for the whole request (not per file).
        result.Outcome.Should().Be(QuarantineDeleteOutcome.AuthorizationDenied);
        await fixture.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.UserScopedB2DeleteClient.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.UserScopedB2DeleteClient.DidNotReceive().DeleteFileVersionAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.DeleteAuthorizationDenied &&
                e.TabModule == "Quarantine" &&
                e.UserId == 1 &&
                e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-1: live authorization success preserves all existing per-file behavior -----------------

    [Fact]
    public async Task Test_SC1_LiveAuthorizationSuccessPreservesExistingBehavior()
    {
        // Arrange
        var fixture = new Fixture();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        ArrangeFullSuccess(fixture, actingUserId: 12, entry);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 12, CancellationToken.None);

        // Assert — identical shape/outcome to CAS-008's pre-existing full-success test (SC-3 above):
        // 200 Completed, per-file Success, Deleted audit row, status transitioned.
        result.Outcome.Should().Be(QuarantineDeleteOutcome.Completed);
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");
        await fixture.QuarantineEntryRepository.Received(1).TryTransitionToDeletedAsync(
            100801, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.Deleted && e.FilePath == entry.Path),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-2: a revoked key (live check throws) touches zero files, 403-shaped denial ---------------

    [Fact]
    public async Task Test_SC2_RevokedKeyAbortsBeforeAnyFileTouched()
    {
        // Arrange — same failure mode as V8, asserted here specifically against the "revoked key"
        // framing (§10 SC-2) rather than V8's generic whole-batch framing.
        var fixture = new Fixture();
        fixture.UserRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ActingUser(1));
        fixture.CredentialProtector.Unprotect(CipherKeyId).Returns(PlainKeyId);
        fixture.CredentialProtector.Unprotect(CipherApplicationKey).Returns(PlainApplicationKey);
        fixture.UserB2KeyAuthorizer.AuthorizeAsync(PlainKeyId, PlainApplicationKey, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "the key has been revoked"));
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineDeleteOutcome.AuthorizationDenied);
        await fixture.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.DeleteAuthorizationDenied),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-3: live capabilities lack deleteFiles (downgraded, key not revoked) — same rejection ----

    [Fact]
    public async Task Test_SC3_DowngradedCapabilityRejected()
    {
        // Arrange — the live authorization call itself SUCCEEDS, but its capability set no longer
        // includes deleteFiles (e.g. downgraded to Read Only at the B2 side mid-session).
        var fixture = new Fixture();
        fixture.UserRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ActingUser(1));
        fixture.CredentialProtector.Unprotect(CipherKeyId).Returns(PlainKeyId);
        fixture.CredentialProtector.Unprotect(CipherApplicationKey).Returns(PlainApplicationKey);
        fixture.UserB2KeyAuthorizer.AuthorizeAsync(PlainKeyId, PlainApplicationKey, Arg.Any<CancellationToken>())
            .Returns(LiveAuthorization("listFiles", "readFiles")); // no deleteFiles
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — identical rejection to SC-2, even though the live call itself succeeded.
        result.Outcome.Should().Be(QuarantineDeleteOutcome.AuthorizationDenied);
        await fixture.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.UserScopedB2DeleteClient.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.DeleteAuthorizationDenied),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-4: the stale login-time claim alone is insufficient — live check still gates delete ----

    [Fact]
    public async Task Test_SC4_StaleClaimDoesNotBypassLiveCheck()
    {
        // Arrange — this call reaching DeleteAsync at all already models a session holding the
        // stale CachetFullAccess claim (that claim is what let the controller's [Authorize] policy
        // admit the request in the first place — see QuarantineController/CAS-018 §2). The entries
        // themselves are configured to otherwise resolve and would succeed if the live check were
        // skipped, proving the stale claim is not what gates this outcome — the live check is.
        var fixture = new Fixture();
        fixture.UserRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ActingUser(1));
        fixture.CredentialProtector.Unprotect(CipherKeyId).Returns(PlainKeyId);
        fixture.CredentialProtector.Unprotect(CipherApplicationKey).Returns(PlainApplicationKey);
        fixture.UserB2KeyAuthorizer.AuthorizeAsync(PlainKeyId, PlainApplicationKey, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "revoked after login"));
        var entryOne = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        var entryTwo = ActiveEntry(100802, fileIndexId: 200802, path: "contracts/2020/invoice-009.pdf", name: "invoice-009.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entryOne);
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100802, Arg.Any<CancellationToken>()).Returns(entryTwo);
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801, 100802 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — denied for the whole batch despite both entries being otherwise fully resolvable
        // and deletable; neither file's per-file pipeline (B2/local/status-transition) ever runs.
        result.Outcome.Should().Be(QuarantineDeleteOutcome.AuthorizationDenied);
        await fixture.UserScopedB2DeleteClient.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-5: a mid-loop revocation (between individual DeleteFileVersionAsync calls) maps onto ----
    // ---- the existing DeleteFailedB2 per-file path, not a new/undefined state ------------------------

    [Fact]
    public async Task Test_SC5_MidLoopRevocationMapsToExistingDeleteFailedB2()
    {
        // Arrange — the pre-batch live check succeeds; the key is then revoked at B2's side in the
        // window between the first and second DeleteFileVersionAsync call within one file's own
        // version loop (CAS-018 §2's explicit scenario — not caught by the pre-batch check at all).
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SinglePage(entry.Path, "4_z_v1", "4_z_v2"));
        fixture.UserScopedB2DeleteClient
            .DeleteFileVersionAsync(Arg.Any<B2AuthorizationInfo>(), entry.Path, "4_z_v1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        fixture.UserScopedB2DeleteClient
            .DeleteFileVersionAsync(Arg.Any<B2AuthorizationInfo>(), entry.Path, "4_z_v2", Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "revoked mid-loop"));
        var sut = fixture.CreateSut();
        var request = new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true };

        // Act
        var result = await sut.DeleteAsync(request, actingUserId: 1, CancellationToken.None);

        // Assert — the caller's existing per-file catch (Step 1 of DeleteAsync's per-file loop)
        // handles this exactly as any other B2ApiException: DeleteFailedB2, not a new outcome or
        // exception type; no local mutation or status transition for this file.
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "b2-delete-failed");
        await fixture.FileIndexRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.DeleteFailedB2 &&
                e.FilePath == entry.Path &&
                e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-6: every other B2-touching consumer is provably unaffected -------------------------------

    [Fact]
    public void Test_SC6_OtherB2ConsumersUnchanged()
    {
        // This service no longer depends on IB2Client at all (CAS-018 §5) — reflection proves its
        // constructor carries no IB2Client parameter, so it is structurally impossible for this
        // change to have altered how IB2Client is consumed here. IB2Client's own contract (used by
        // Search cross-check, Sync Copy/Update/Purge, mirror population, and the Worker task
        // processors) is asserted unchanged by the pre-existing, unmodified B2ClientTests.cs (CAS-001)
        // — that class compiling and passing without any edit in this change IS the proof for those
        // other five consumers, per this run's scope note; it is not duplicated here.
        var constructorParameterTypes = typeof(QuarantinePermanentDeleteService)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();

        constructorParameterTypes.Should().NotContain(typeof(IB2Client));
        constructorParameterTypes.Should().Contain(typeof(IUserScopedB2DeleteClient));

        // IB2Client's own member surface (the seven §6.1 operations) is unchanged — same seven
        // methods as before this spec, none removed/renamed/re-signed.
        var ib2ClientMethodNames = typeof(IB2Client).GetMethods().Select(m => m.Name).OrderBy(n => n).ToList();
        ib2ClientMethodNames.Should().BeEquivalentTo(new[]
        {
            nameof(IB2Client.AuthorizeAsync),
            nameof(IB2Client.ListFileNamesAsync),
            nameof(IB2Client.ListFileVersionsAsync),
            nameof(IB2Client.GetFileInfoAsync),
            nameof(IB2Client.DownloadFileByIdAsync),
            nameof(IB2Client.GetUploadUrlAsync),
            nameof(IB2Client.UploadFileAsync),
            nameof(IB2Client.DeleteFileVersionAsync)
        });
    }

    // ---- SC-7: the user's live B2 key/token is never logged, in whole or in part --------------------

    [Fact]
    public async Task Test_SC7_NoKeyOrTokenLeakageInLogs()
    {
        // Arrange — exercise both the success path (full delete) and the failure path (denied),
        // each carrying its own raw secret material, and inspect every formatted log message this
        // service's own ILogger recorded across both calls.
        var fixture = new Fixture();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "contracts/2019/agreement-004.pdf", name: "agreement-004.pdf");
        ArrangeFullSuccess(fixture, actingUserId: 1, entry);
        var sut = fixture.CreateSut();

        // Act — Part 1: successful delete, using a live authorization carrying LiveAuthToken and
        // the user's decrypted PlainKeyId/PlainApplicationKey.
        await sut.DeleteAsync(new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }, actingUserId: 1, CancellationToken.None);

        // Act — Part 2: a denied delete for a second user, whose decrypted key material must also
        // never appear in any log line even though the whole point of this path is to log the denial.
        const string secondPlainKeyId = "plain-key-id-second-user-0002";
        const string secondPlainAppKey = "K002SecondUserPlainApplicationKeyNeverLog";
        fixture.UserRepository.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = 2,
            Username = "second-user",
            PasswordHash = "irrelevant-hash",
            B2ApplicationKeyId = "ct:keyid-2",
            B2ApplicationKey = "ct:key-2",
            IsActive = true,
            SessionTokenVersion = 0
        });
        fixture.CredentialProtector.Unprotect("ct:keyid-2").Returns(secondPlainKeyId);
        fixture.CredentialProtector.Unprotect("ct:key-2").Returns(secondPlainAppKey);
        fixture.UserB2KeyAuthorizer.AuthorizeAsync(secondPlainKeyId, secondPlainAppKey, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(401, "bad_auth_token", "revoked"));
        await sut.DeleteAsync(new QuarantineDeleteRequest { FileIds = new[] { 100802 }, Confirm = true }, actingUserId: 2, CancellationToken.None);

        // Assert — none of the plaintext key material or the live authorization token appears in
        // any formatted log message this service wrote (CAS-018 §11, extending CAS-001 SC-5 /
        // CAS-002 §11's existing no-key-leakage rule to this new call site).
        var loggedMessages = GetFormattedLogMessages(fixture.Logger);
        loggedMessages.Should().NotBeEmpty();
        loggedMessages.Should().OnlyContain(m =>
            !m.Contains(PlainKeyId) && !m.Contains(PlainApplicationKey) && !m.Contains(LiveAuthToken) &&
            !m.Contains(secondPlainKeyId) && !m.Contains(secondPlainAppKey) &&
            !m.Contains(CipherKeyId) && !m.Contains(CipherApplicationKey));
    }

    /// <summary>Extracts the formatted message from every call NSubstitute recorded against an
    /// <c>ILogger&lt;T&gt;</c> mock (mirrors <c>B2ClientTests</c>/<c>UserB2KeyAuthorizerTests</c>'s
    /// identical helper).</summary>
    private static List<string> GetFormattedLogMessages(ILogger<QuarantinePermanentDeleteService> logger)
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

    // ---- CAS-062: Permanent Delete also removes the local B2 mirror copy ----------------------------

    [Fact]
    public async Task Test_CAS062_PermanentDelete_RemovesLocalMirrorFile()
    {
        // Arrange
        var fixture = new Fixture();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "D/Shares/Operations/Gold Chain/box.pdf", name: "box.pdf");
        ArrangeFullSuccess(fixture, actingUserId: 1, entry);
        var calls = new List<string>();
        fixture.B2MirrorFileRemover.RemoveAsync(entry.Path, Arg.Any<CancellationToken>())
            .Returns(_ => { calls.Add("remove-local"); return B2MirrorRemoveResult.Removed; });
        fixture.QuarantineEntryRepository.TryTransitionToDeletedAsync(entry.QuarantineEntryId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { calls.Add("status-deleted"); return true; });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.DeleteAsync(new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }, 1, CancellationToken.None);

        // Assert — local copy removed (with the B2 key) before the status transition; success.
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");
        calls.Should().Equal("remove-local", "status-deleted");
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.Deleted && e.Result == AuditResult.Success), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_CAS062_PermanentDelete_B2Failure_LocalUntouched()
    {
        // Arrange
        var fixture = new Fixture();
        ArrangeLiveAuthorizationSucceeds(fixture, actingUserId: 1);
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "D/Shares/Operations/Gold Chain/box.pdf", name: "box.pdf");
        fixture.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        fixture.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), BucketId, entry.Path, entry.Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "B2 call failed."));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.DeleteAsync(new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }, 1, CancellationToken.None);

        // Assert
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Reason == "b2-delete-failed");
        await fixture.B2MirrorFileRemover.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_CAS062_PermanentDelete_LocalRemovalFailed_EntryStaysActive()
    {
        // Arrange
        var fixture = new Fixture();
        var entry = ActiveEntry(100801, fileIndexId: 200801, path: "D/Shares/Operations/Gold Chain/box.pdf", name: "box.pdf");
        ArrangeFullSuccess(fixture, actingUserId: 1, entry);
        fixture.B2MirrorFileRemover.RemoveAsync(entry.Path, Arg.Any<CancellationToken>())
            .Returns(B2MirrorRemoveResult.Failed("Local mirror copy could not be removed."));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.DeleteAsync(new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }, 1, CancellationToken.None);

        // Assert — Failed/local-delete-failed, DeleteFailedLocal audited, status NOT transitioned.
        result.Response!.Results.Should().ContainSingle(r =>
            r.FileId == 100801 && r.Result == "Failed" && r.Reason == "local-delete-failed");
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.DeleteFailedLocal
                && e.Result == AuditResult.Failure
                && e.ResultDetail!.StartsWith("Deleted from B2; local mirror copy not removed:")),
            Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.Deleted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_CAS062_PermanentDelete_RemoverThrows_FileFailsBatchContinues()
    {
        // Arrange — review F-2: two files; the remover throws for the first, succeeds for the second.
        var fixture = new Fixture();
        var first = ActiveEntry(100801, fileIndexId: 200801, path: "D/Shares/Operations/a.pdf", name: "a.pdf");
        var second = ActiveEntry(100802, fileIndexId: 200802, path: "D/Shares/Operations/b.pdf", name: "b.pdf");
        ArrangeFullSuccess(fixture, actingUserId: 1, first);
        ArrangeFullSuccess(fixture, actingUserId: 1, second, b2FileId: "4_z_v2");
        fixture.B2MirrorFileRemover.RemoveAsync(first.Path, Arg.Any<CancellationToken>())
            .Throws(new InvalidDataException("simulated database failure"));
        fixture.B2MirrorFileRemover.RemoveAsync(second.Path, Arg.Any<CancellationToken>()).Returns(B2MirrorRemoveResult.Removed);
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.DeleteAsync(
            new QuarantineDeleteRequest { FileIds = new[] { 100801, 100802 }, Confirm = true }, 1, CancellationToken.None);

        // Assert — no exception escapes; first is local-delete-failed (still Active), second succeeds.
        var result = (await act.Should().NotThrowAsync()).Which;
        result.Response!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Reason == "local-delete-failed");
        result.Response.Results.Should().ContainSingle(r => r.FileId == 100802 && r.Result == "Success");
        await fixture.QuarantineEntryRepository.DidNotReceive().TryTransitionToDeletedAsync(100801, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_CAS062_PermanentDelete_ODriveRow_RemovesB2MirrorKeyOnly()
    {
        // Arrange — the quarantined row is an O-Drive row; its relative_path is the same B2 key.
        // The service only ever hands that key to the B2-mirror remover (which resolves source B2,
        // SC-5) — there is no O-Drive file-system call anywhere in this flow.
        var fixture = new Fixture();
        var entry = ActiveEntry(100801, fileIndexId: 300801, path: "D/Shares/Operations/Gold Chain/box.pdf", name: "box.pdf");
        ArrangeFullSuccess(fixture, actingUserId: 1, entry);
        fixture.B2MirrorFileRemover.RemoveAsync(entry.Path, Arg.Any<CancellationToken>()).Returns(B2MirrorRemoveResult.Removed);
        var sut = fixture.CreateSut();

        // Act
        await sut.DeleteAsync(new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }, 1, CancellationToken.None);

        // Assert — exactly one local removal, for the B2 key; the row soft-delete is unchanged (that row only).
        await fixture.B2MirrorFileRemover.Received(1).RemoveAsync(entry.Path, Arg.Any<CancellationToken>());
        await fixture.B2MirrorFileRemover.Received(1).RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.Received(1).UpdateAsync(
            300801, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Is<bool?>(b => b == false), Arg.Any<CancellationToken>());
    }
}
