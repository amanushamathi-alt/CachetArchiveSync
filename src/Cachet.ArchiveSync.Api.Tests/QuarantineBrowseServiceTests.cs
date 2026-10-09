using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="QuarantineBrowseService"/> (CAS-050 §2/§8/§9/§13 "QuarantineBrowseServiceTests"):
/// audit-before-response ordering, count mapping, 404/409 mapping, request-shape validation (V1) and
/// state pass-through. The repository/stored-procedure behaviour itself (prefix boundary, LIKE escape,
/// case-insensitivity, snapshot, all-origin remove, 50k set-based) is proven against a real SQL Server in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.QuarantineBrowseRepositoryTests</c>.
/// </summary>
public sealed class QuarantineBrowseServiceTests
{
    private const int ActingUserId = 7;
    private const string Prefix = "D/Shares/Operations";
    private const string FullOpsAudit = Prefix + "/Ops/Audit";

    private sealed class Fixture
    {
        public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();

        public IB2PathPrefixProvider PrefixProvider { get; } = Substitute.For<IB2PathPrefixProvider>();

        public Fixture()
        {
            // CAS-053: the production prefix is static and never empty; the request bucket "bkt" is mapped.
            PrefixProvider.GetPrefixAsync("bkt", Arg.Any<CancellationToken>()).Returns(Prefix);
        }

        public QuarantineBrowseService CreateSut() =>
            new(QuarantineEntryRepository, FileIndexRepository, AuditLogService, PrefixProvider, NullLogger<QuarantineBrowseService>.Instance);

        public void SeedActiveFile(int fileIndexId, string relativePath = "Ops/Audit/a.pdf", bool isActive = true) =>
            FileIndexRepository.GetAsync(fileIndexId, Arg.Any<CancellationToken>()).Returns(new FileIndexRecord
            {
                FileIndexId = fileIndexId,
                Source = "B2",
                BucketName = "bkt",
                RelativePath = relativePath,
                FileName = relativePath.Split('/').Last(),
                FileSizeBytes = 1,
                ModifiedAt = DateTimeOffset.UtcNow,
                IsActive = isActive
            });
    }

    private static QuarantineBrowseRequest FileRequest(int fileIndexId = 600801) =>
        new() { Scope = "File", FileIndexId = fileIndexId };

    private static QuarantineBrowseRequest FolderRequest(string folderPath = "Ops/Audit") =>
        new() { Scope = "Folder", Source = "B2", BucketName = "bkt", FolderPath = folderPath };

    // ---- SC-1 (service level): single-file add inserts one row and audits once ---------------------------

    [Fact]
    public async Task Test_SC1_AddFile_ServiceInsertsOneRowAuditsOnceReturnsAddedOne()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.SeedActiveFile(600801, "Ops/Audit/a.pdf");
        fixture.QuarantineEntryRepository
            .InsertBrowseFileAsync("7", "7", 600801, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.Added, QuarantineEntryId = 100900 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.AddAsync(FileRequest(), ActingUserId, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineBrowseAddOutcome.Completed);
        result.Response!.Added.Should().Be(1);
        result.Response.AlreadyQuarantined.Should().Be(0);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineAdd &&
                e.TabModule == "Quarantine" &&
                e.UserId == ActingUserId &&
                e.FilePath == "Ops/Audit/a.pdf" &&
                e.ResultDetail == "scope=File; added=1; alreadyQuarantined=0" &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-2 (service level): folder add is ONE repository call, count = SP's @added -------------------

    [Fact]
    public async Task Test_SC2_AddFolder_ServiceMakesOneSetBasedCallAndReportsAddedCount()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync("7", "7", "B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 50_000, AlreadyQuarantined = 0 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.AddAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert - no per-file round trips: exactly one folder call, zero single-file inserts, zero per-file lookups.
        result.Response!.Added.Should().Be(50_000);
        await fixture.QuarantineEntryRepository.Received(1).InsertBrowseFolderAsync(
            "7", "7", "B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().InsertBrowseFileAsync(default!, default!, default, default);
        await fixture.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default!, default, default);
        await fixture.FileIndexRepository.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    // ---- SC-7: exactly one audit row per action (not per file), before the success result ------------------

    [Fact]
    public async Task Test_SC7_Audit_OnePerAction_BeforeResponse()
    {
        // Arrange
        var fixture = new Fixture();
        var callOrder = new List<string>();
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 3, AlreadyQuarantined = 2 })
            .AndDoes(_ => callOrder.Add("insert"));
        fixture.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync("B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(5)
            .AndDoes(_ => callOrder.Add("delete"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();

        // Act
        var add = await sut.AddAsync(FolderRequest(), ActingUserId, CancellationToken.None);
        callOrder.Add("add-returned");
        var remove = await sut.RemoveAsync(FolderRequest(), ActingUserId, CancellationToken.None);
        callOrder.Add("remove-returned");

        // Assert - one audit row per action (two actions, two rows, regardless of 5 / 5 files affected),
        // always between the mutation and the returned result (FR-23).
        callOrder.Should().Equal("insert", "audit", "add-returned", "delete", "audit", "remove-returned");
        add.Outcome.Should().Be(QuarantineBrowseAddOutcome.Completed);
        remove.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.Completed);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineAdd && e.TabModule == "Quarantine" && e.FilePath == FullOpsAudit &&
                e.ResultDetail == "scope=Folder; added=3; alreadyQuarantined=2"),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineRemoved && e.TabModule == "Quarantine" && e.FilePath == FullOpsAudit &&
                e.ResultDetail == "scope=Folder; removed=5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC7_Audit_Failure_PropagatesSoNoSuccessResultIsReturned_Add()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 1, AlreadyQuarantined = 0 });
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("audit store down"));
        var sut = fixture.CreateSut();

        // Act
        Func<Task> act = () => sut.AddAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Test_SC7_Audit_Failure_PropagatesSoNoSuccessResultIsReturned_Remove()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync("B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(2);
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("audit store down"));
        var sut = fixture.CreateSut();

        // Act
        Func<Task> act = () => sut.RemoveAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- V1: request shape ------------------------------------------------------------------------------

    public static IEnumerable<object?[]> InvalidShapes() => new[]
    {
        new object?[] { new QuarantineBrowseRequest { Scope = null } },
        new object?[] { new QuarantineBrowseRequest { Scope = "file", FileIndexId = 1 } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Both", FileIndexId = 1 } },
        new object?[] { new QuarantineBrowseRequest { Scope = "File" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "S3", BucketName = "b", FolderPath = "Ops" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "b2", BucketName = "b", FolderPath = "Ops" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = null, BucketName = "b", FolderPath = "Ops" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = " ", FolderPath = "Ops" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = null, FolderPath = "Ops" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "b", FolderPath = "" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "b", FolderPath = null } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "b", FolderPath = "/Ops" } },
        new object?[] { new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "b", FolderPath = "Ops/" } }
    };

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task Test_V1_BrowseService_InvalidShape_ReturnsValidationFailedWithoutTouchingStores(QuarantineBrowseRequest request)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var add = await sut.AddAsync(request, ActingUserId, CancellationToken.None);
        var remove = await sut.RemoveAsync(request, ActingUserId, CancellationToken.None);

        // Assert
        add.Outcome.Should().Be(QuarantineBrowseAddOutcome.ValidationFailed);
        remove.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.ValidationFailed);
        fixture.QuarantineEntryRepository.ReceivedCalls().Should().BeEmpty();
        fixture.FileIndexRepository.ReceivedCalls().Should().BeEmpty();
        fixture.AuditLogService.ReceivedCalls().Should().BeEmpty();
    }

    // ---- V3: unknown file / empty folder -> not-found outcomes, no audit -------------------------------

    [Fact]
    public async Task Test_V3_Browse_UnknownTarget_404()
    {
        // Arrange
        var fixture = new Fixture();
        // (a) File scope add: no file_index row at all -> FileNotFound (fixture default: GetAsync returns null).
        // (b) File scope add: row exists but is inactive.
        fixture.SeedActiveFile(2, isActive: false);
        // (c) File scope add: lookup passes but the SP says "not found or not active" (deactivated since).
        fixture.SeedActiveFile(3);
        fixture.QuarantineEntryRepository
            .InsertBrowseFileAsync(Arg.Any<string>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.SearchResultNotFound });
        // (d) Folder scope add / remove: the SP reports "folder holds no active files" (repository returns null).
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((QuarantineBrowseFolderAddRow?)null);
        fixture.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);
        // (e) File scope remove: no Active row (SP deleted 0).
        fixture.QuarantineEntryRepository.HardDeleteBrowseFileAsync(9, Arg.Any<CancellationToken>()).Returns(0);
        var sut = fixture.CreateSut();

        // Act
        var missing = await sut.AddAsync(FileRequest(1), ActingUserId, CancellationToken.None);
        var inactive = await sut.AddAsync(FileRequest(2), ActingUserId, CancellationToken.None);
        var raced = await sut.AddAsync(FileRequest(3), ActingUserId, CancellationToken.None);
        var emptyFolderAdd = await sut.AddAsync(FolderRequest("Nope"), ActingUserId, CancellationToken.None);
        var emptyFolderRemove = await sut.RemoveAsync(FolderRequest("Nope"), ActingUserId, CancellationToken.None);
        var notQuarantined = await sut.RemoveAsync(FileRequest(9), ActingUserId, CancellationToken.None);

        // Assert
        missing.Outcome.Should().Be(QuarantineBrowseAddOutcome.FileNotFound);
        inactive.Outcome.Should().Be(QuarantineBrowseAddOutcome.FileNotFound);
        raced.Outcome.Should().Be(QuarantineBrowseAddOutcome.FileNotFound);
        emptyFolderAdd.Outcome.Should().Be(QuarantineBrowseAddOutcome.FolderNotFound);
        emptyFolderRemove.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.FolderNotFound);
        notQuarantined.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.NotQuarantined);
        missing.Response.Should().BeNull();
        await fixture.AuditLogService.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
        // Missing / inactive files are rejected before any insert is attempted.
        await fixture.QuarantineEntryRepository.DidNotReceive().InsertBrowseFileAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceive().InsertBrowseFileAsync(Arg.Any<string>(), Arg.Any<string>(), 2, Arg.Any<CancellationToken>());
    }

    // ---- V4: File scope conflict -> 409 outcome; Folder scope skips and counts ----------------------------

    [Fact]
    public async Task Test_V4_AlreadyQuarantined_FileConflict_FolderSkips()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.SeedActiveFile(600801);
        fixture.QuarantineEntryRepository
            .InsertBrowseFileAsync(Arg.Any<string>(), Arg.Any<string>(), 600801, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.AlreadyActive });
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 1, AlreadyQuarantined = 4 });
        var sut = fixture.CreateSut();

        // Act
        var file = await sut.AddAsync(FileRequest(600801), ActingUserId, CancellationToken.None);
        var folder = await sut.AddAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert - File scope: conflict, no audit. Folder scope: 200-style completion with the skip count, audited once.
        file.Outcome.Should().Be(QuarantineBrowseAddOutcome.AlreadyQuarantined);
        file.Response.Should().BeNull();
        folder.Outcome.Should().Be(QuarantineBrowseAddOutcome.Completed);
        folder.Response!.Added.Should().Be(1);
        folder.Response.AlreadyQuarantined.Should().Be(4);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.ResultDetail == "scope=Folder; added=1; alreadyQuarantined=4"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V4_FolderWhereEverythingAlreadyQuarantined_CompletesWithAddedZero()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 0, AlreadyQuarantined = 9 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.AddAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineBrowseAddOutcome.Completed);
        result.Response!.Added.Should().Be(0);
        result.Response.AlreadyQuarantined.Should().Be(9);
    }

    // ---- SC-4 (service level): folder add reports the skipped (already-quarantined) count -------------------

    [Fact]
    public async Task Test_SC4_FolderAdd_ServiceMapsAlreadyQuarantinedCount()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 2, AlreadyQuarantined = 3 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.AddAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert
        result.Response!.Should().BeEquivalentTo(new QuarantineBrowseAddResponse { Added = 2, AlreadyQuarantined = 3 });
    }

    // ---- SC-5 (service level): deselect maps removed counts; file path resolved for the audit row ------------

    [Fact]
    public async Task Test_SC5_Deselect_ServiceFileAndFolderRemoveCounts()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.SeedActiveFile(600801, "Ops/Audit/a.pdf");
        fixture.QuarantineEntryRepository.HardDeleteBrowseFileAsync(600801, Arg.Any<CancellationToken>()).Returns(1);
        fixture.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync("B2", "bkt", FullOpsAudit, Arg.Any<CancellationToken>()).Returns(0);
        var sut = fixture.CreateSut();

        // Act
        var file = await sut.RemoveAsync(FileRequest(600801), ActingUserId, CancellationToken.None);
        var folder = await sut.RemoveAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert - file: removed=1 with the file path audited; folder with nothing quarantined: 200-style removed=0.
        file.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.Completed);
        file.Response!.Removed.Should().Be(1);
        folder.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.Completed);
        folder.Response!.Removed.Should().Be(0);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineRemoved && e.FilePath == "Ops/Audit/a.pdf" && e.ResultDetail == "scope=File; removed=1"),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.ResultDetail == "scope=Folder; removed=0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC5_Deselect_FileRowMissingFromIndexStillAuditsWithNullPath()
    {
        // Arrange - tolerated edge: the entry existed (1 row deleted) but the file_index lookup returns nothing.
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository.HardDeleteBrowseFileAsync(5, Arg.Any<CancellationToken>()).Returns(1);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RemoveAsync(FileRequest(5), ActingUserId, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.Completed);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.FilePath == null && e.ResultDetail == "scope=File; removed=1"),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-9 (service level): state validation + pass-through ---------------------------------------------

    [Fact]
    public async Task Test_SC9_State_ServicePassesThroughRepositoryResponse_EmptyFolderPathIsBucketRoot()
    {
        // Arrange
        var fixture = new Fixture();
        var state = new QuarantineBrowseStateResponse
        {
            FileIndexIds = new[] { 11, 12 },
            Folders = new[] { new QuarantineBrowseStateFolder { FolderName = "Ops", TotalFiles = 10, QuarantinedFiles = 4 } }
        };
        fixture.QuarantineEntryRepository.GetBrowseStateAsync("B2", "bkt", Prefix, Arg.Any<CancellationToken>()).Returns(state);
        var sut = fixture.CreateSut();

        // Act
        var rootNull = await sut.GetStateAsync("B2", "bkt", null, CancellationToken.None);
        var rootEmpty = await sut.GetStateAsync("B2", "bkt", "", CancellationToken.None);

        // Assert
        rootNull.Outcome.Should().Be(QuarantineBrowseStateOutcome.Completed);
        rootNull.Response.Should().BeSameAs(state);
        rootEmpty.Response.Should().BeSameAs(state);
    }

    [Theory]
    [InlineData(null, "bkt", "Ops")]
    [InlineData("S3", "bkt", "Ops")]
    [InlineData("B2", "", "Ops")]
    [InlineData("B2", null, "Ops")]
    [InlineData("B2", "bkt", "/Ops")]
    [InlineData("B2", "bkt", "Ops/")]
    public async Task Test_SC9_State_InvalidShape_ReturnsValidationFailed(string? source, string? bucket, string? folderPath)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetStateAsync(source, bucket, folderPath, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineBrowseStateOutcome.ValidationFailed);
        result.Response.Should().BeNull();
        fixture.QuarantineEntryRepository.ReceivedCalls().Should().BeEmpty();
    }

    // ---- CAS-053: B2 path prefix resolution --------------------------------------------------------------

    // V1 / SC-1
    [Fact]
    public async Task Test_SC1_Add_UsesFullPath()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 1, AlreadyQuarantined = 0 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.AddAsync(FolderRequest("Ops/Audit"), ActingUserId, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineBrowseAddOutcome.Completed);
        await fixture.PrefixProvider.Received(1).GetPrefixAsync("bkt", Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.Received(1).InsertBrowseFolderAsync(
            "7", "7", "B2", "bkt", "D/Shares/Operations/Ops/Audit", Arg.Any<CancellationToken>());
    }

    // SC-2
    [Fact]
    public async Task Test_SC2_Remove_UsesFullPath()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync("B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(2);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.RemoveAsync(FolderRequest("Ops/Audit"), ActingUserId, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.Completed);
        await fixture.QuarantineEntryRepository.Received(1).HardDeleteBrowseFolderAsync(
            "B2", "bkt", "D/Shares/Operations/Ops/Audit", Arg.Any<CancellationToken>());
    }

    // SC-3
    [Theory]
    [InlineData("", "D/Shares/Operations")]
    [InlineData(null, "D/Shares/Operations")]
    [InlineData("Ops", "D/Shares/Operations/Ops")]
    public async Task Test_SC3_State_RootAndSubfolder(string? folderPath, string expectedFullPath)
    {
        // Arrange
        var fixture = new Fixture();
        var state = new QuarantineBrowseStateResponse { FileIndexIds = Array.Empty<int>(), Folders = Array.Empty<QuarantineBrowseStateFolder>() };
        fixture.QuarantineEntryRepository
            .GetBrowseStateAsync("B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(state);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetStateAsync("B2", "bkt", folderPath, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(QuarantineBrowseStateOutcome.Completed);
        await fixture.QuarantineEntryRepository.Received(1).GetBrowseStateAsync(
            "B2", "bkt", expectedFullPath, Arg.Any<CancellationToken>());
    }

    // SC-4
    [Fact]
    public async Task Test_SC4_Audit_FullPath()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 2, AlreadyQuarantined = 1 });
        fixture.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync("B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(3);
        var sut = fixture.CreateSut();

        // Act
        await sut.AddAsync(FolderRequest("Ops/Audit"), ActingUserId, CancellationToken.None);
        await sut.RemoveAsync(FolderRequest("Ops/Audit"), ActingUserId, CancellationToken.None);

        // Assert
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineAdd && e.FilePath == "D/Shares/Operations/Ops/Audit"),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineRemoved && e.FilePath == "D/Shares/Operations/Ops/Audit"),
            Arg.Any<CancellationToken>());
    }

    // SC-6 (service level)
    [Fact]
    public async Task Test_SC6_MappingMissing_500()
    {
        // Arrange - no mapping row for "unmapped": provider returns null.
        var fixture = new Fixture();
        fixture.PrefixProvider.GetPrefixAsync("unmapped", Arg.Any<CancellationToken>()).Returns((string?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "unmapped", FolderPath = "Ops" };

        // Act
        var add = await sut.AddAsync(request, ActingUserId, CancellationToken.None);
        var remove = await sut.RemoveAsync(request, ActingUserId, CancellationToken.None);
        var state = await sut.GetStateAsync("B2", "unmapped", "Ops", CancellationToken.None);

        // Assert
        add.Outcome.Should().Be(QuarantineBrowseAddOutcome.MappingMissing);
        remove.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.MappingMissing);
        state.Outcome.Should().Be(QuarantineBrowseStateOutcome.MappingMissing);
        state.Response.Should().BeNull();
        fixture.QuarantineEntryRepository.ReceivedCalls().Should().BeEmpty();
        fixture.AuditLogService.ReceivedCalls().Should().BeEmpty();
    }

    // V2
    [Fact]
    public async Task Test_V2_MappingMissing_NoSideEffects()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.PrefixProvider.GetPrefixAsync("unmapped", Arg.Any<CancellationToken>()).Returns((string?)null);
        var sut = fixture.CreateSut();
        var request = new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "unmapped", FolderPath = "Ops" };

        // Act
        await sut.AddAsync(request, ActingUserId, CancellationToken.None);
        await sut.RemoveAsync(request, ActingUserId, CancellationToken.None);
        await sut.GetStateAsync("B2", "unmapped", "", CancellationToken.None);

        // Assert - fails closed: never falls back to the unprefixed path; no repository or audit call of any kind.
        fixture.QuarantineEntryRepository.ReceivedCalls().Should().BeEmpty();
        fixture.FileIndexRepository.ReceivedCalls().Should().BeEmpty();
        fixture.AuditLogService.ReceivedCalls().Should().BeEmpty();
    }

    // V1 (prefix applied, bucket taken from the request)
    [Fact]
    public async Task Test_V1_B2FolderPath_PrefixApplied()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.PrefixProvider.GetPrefixAsync("other", Arg.Any<CancellationToken>()).Returns("X/Y");
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "other", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 1, AlreadyQuarantined = 0 });
        var sut = fixture.CreateSut();
        var request = new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "other", FolderPath = "Ops" };

        // Act
        await sut.AddAsync(request, ActingUserId, CancellationToken.None);

        // Assert - the REQUEST bucket's prefix is used, applied before the repository call.
        await fixture.PrefixProvider.Received(1).GetPrefixAsync("other", Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.Received(1).InsertBrowseFolderAsync(
            "7", "7", "B2", "other", "X/Y/Ops", Arg.Any<CancellationToken>());
    }

    // V3
    [Fact]
    public async Task Test_V3_FullPathTooLong_422()
    {
        // Arrange - incoming path passes the shape rules; prefix + "/" + folder exceeds 1000 characters.
        var fixture = new Fixture();
        var tooLong = new string('a', 1000 - Prefix.Length);      // full length = 1001
        var atLimit = new string('a', 1000 - Prefix.Length - 1);  // full length = 1000
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 1, AlreadyQuarantined = 0 });
        var sut = fixture.CreateSut();

        // Act
        var add = await sut.AddAsync(FolderRequest(tooLong), ActingUserId, CancellationToken.None);
        var remove = await sut.RemoveAsync(FolderRequest(tooLong), ActingUserId, CancellationToken.None);
        var state = await sut.GetStateAsync("B2", "bkt", tooLong, CancellationToken.None);
        var addAtLimit = await sut.AddAsync(FolderRequest(atLimit), ActingUserId, CancellationToken.None);

        // Assert
        add.Outcome.Should().Be(QuarantineBrowseAddOutcome.ValidationFailed);
        remove.Outcome.Should().Be(QuarantineBrowseRemoveOutcome.ValidationFailed);
        state.Outcome.Should().Be(QuarantineBrowseStateOutcome.ValidationFailed);
        addAtLimit.Outcome.Should().Be(QuarantineBrowseAddOutcome.Completed);
        await fixture.QuarantineEntryRepository.Received(1).InsertBrowseFolderAsync(
            Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().HardDeleteBrowseFolderAsync(default!, default!, default!, default);
        await fixture.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().GetBrowseStateAsync(default!, default!, default!, default);
    }

    // SC-8
    [Fact]
    public async Task Test_SC8_NoPrefixInResponse()
    {
        // Arrange - repository returns leaf names and ids only (as the SP does); the service must not add the prefix.
        var fixture = new Fixture();
        var state = new QuarantineBrowseStateResponse
        {
            FileIndexIds = new[] { 11 },
            Folders = new[] { new QuarantineBrowseStateFolder { FolderName = "Ops", TotalFiles = 4, QuarantinedFiles = 1 } }
        };
        fixture.QuarantineEntryRepository
            .GetBrowseStateAsync("B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(state);
        fixture.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 1, AlreadyQuarantined = 0 });
        fixture.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync("B2", "bkt", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);
        var sut = fixture.CreateSut();

        // Act
        var stateResult = await sut.GetStateAsync("B2", "bkt", "", CancellationToken.None);
        var add = await sut.AddAsync(FolderRequest(), ActingUserId, CancellationToken.None);
        var remove = await sut.RemoveAsync(FolderRequest(), ActingUserId, CancellationToken.None);

        // Assert
        var bodies = System.Text.Json.JsonSerializer.Serialize(new object?[] { stateResult.Response, add.Response, remove.Response });
        bodies.Should().NotContain("D/Shares").And.NotContain("Operations");
        stateResult.Response!.Folders.Should().OnlyContain(f => !f.FolderName.Contains('/'));
    }
}
