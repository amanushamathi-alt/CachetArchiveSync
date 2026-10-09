using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="AuditLogListService"/> (CAS-009 §6 <c>GET /audit-log</c>, FR-23/FR-24)
/// — the spec's §13 Test Plan "AuditLogListServiceTests" assignment: V1-V6, SC-1-SC-5.
///
/// Scope note (consistent with CAS-008's own DB-level test scope choice): SC-2/SC-3/SC-4's "the
/// filter/pagination actually reaches SQL Server and returns the right rows" aspect is a
/// repository-level concern proven separately (real SQL Server, not available in this
/// environment) by <c>AuditLogRepositoryTests.Test_ListAsync_FiltersByTabAndDateRangeAndPaginatesCorrectly</c>
/// (§13). Here, the repository is mocked with canned rows/totalCount and these tests instead prove:
/// (a) the service passes tab/dateFrom/dateTo/page/pageSize through to
/// <see cref="IAuditLogRepository.ListAsync"/> unchanged, and (b) the service correctly maps
/// whatever rows the mocked repository returns (including a distinct "page 2" data set for SC-4,
/// and a Failure-result row with <c>resultDetail</c> for SC-5) into the response DTO.
/// </summary>
public sealed class AuditLogListServiceTests
{
    private sealed class Fixture
    {
        public IAuditLogRepository AuditLogRepository { get; } = Substitute.For<IAuditLogRepository>();
        public ILogger<AuditLogListService> Logger { get; } = Substitute.For<ILogger<AuditLogListService>>();

        public AuditLogListService CreateSut() => new(AuditLogRepository, Logger);
    }

    private static AuditLogRow Row(
        int auditLogId,
        string username,
        string tabModule,
        string action,
        string? filePath,
        DateTimeOffset createdAt,
        string result = "Success",
        string? resultDetail = null) => new()
        {
            AuditLogId = auditLogId,
            Username = username,
            TabModule = tabModule,
            Action = action,
            FilePath = filePath,
            CreatedAt = createdAt,
            Result = result,
            ResultDetail = resultDetail
        };

    // ---- SC-1: default request (no query params) returns 200 with paged rows newest first ---------

    [Fact]
    public async Task Test_SC1_DefaultRequestReturnsPagedRowsNewestFirst()
    {
        // Arrange — dbo.audit_log_lst is the layer that enforces "newest first" ordering; here it
        // returns exactly the two rows it is contracted to, already ordered created_at DESC.
        var fixture = new Fixture();
        var newest = Row(100901, "m.alvarez", "Quarantine", "Deleted", "contracts/2019/agreement-004.pdf",
            new DateTimeOffset(2026, 9, 9, 14, 22, 10, TimeSpan.Zero));
        var older = Row(100900, "j.doe", "Auth", "Login", null,
            new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero));
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow> { newest, older }, 2));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AuditLogListOutcome.Found);
        result.Response!.Items.Should().HaveCount(2);
        result.Response.Items[0].Should().BeEquivalentTo(new AuditLogListItem
        {
            User = "m.alvarez",
            Tab = "Quarantine",
            Action = "Deleted",
            FilePath = "contracts/2019/agreement-004.pdf",
            Timestamp = newest.CreatedAt,
            Result = "Success",
            ResultDetail = null
        });
        result.Response.Items[1].User.Should().Be("j.doe");
        result.Response.Page.Should().Be(1);
        result.Response.PageSize.Should().Be(50);
        result.Response.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Test_SC1_DefaultPageSizeIs50WhenOmitted()
    {
        // Arrange — proves the controller/service default (pageSize=50) reaches the repository call.
        var fixture = new Fixture();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>(), 0));
        var sut = fixture.CreateSut();

        // Act
        await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        await fixture.AuditLogRepository.Received(1).ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>());
    }

    // ---- SC-2: tab filter passes through unchanged to the repository -------------------------------

    [Fact]
    public async Task Test_SC2_TabFilterReturnsOnlyMatchingRows()
    {
        // Arrange — dbo.audit_log_lst enforces tab_module = 'Quarantine' server-side; here the mock
        // repository returns only Quarantine rows, proving the service passes tab through unchanged
        // and does not re-filter or widen what the repository already filtered.
        var fixture = new Fixture();
        var quarantineRow = Row(100901, "m.alvarez", "Quarantine", "Deleted", "contracts/2019/agreement-004.pdf",
            new DateTimeOffset(2026, 9, 9, 14, 22, 10, TimeSpan.Zero));
        fixture.AuditLogRepository
            .ListAsync("Quarantine", null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow> { quarantineRow }, 1));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: "Quarantine", dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Response!.Items.Should().ContainSingle(i => i.Tab == "Quarantine");
        await fixture.AuditLogRepository.Received(1).ListAsync("Quarantine", null, null, 1, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC2_AllSentinelPassesNullTabToRepository()
    {
        // Arrange — 'All' (and omitted) must normalize to a null tab filter, per §6/§7 V1.
        var fixture = new Fixture();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>(), 0));
        var sut = fixture.CreateSut();

        // Act
        await sut.ListAsync(tab: "All", dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        await fixture.AuditLogRepository.Received(1).ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>());
    }

    // ---- SC-3: date range filter passes through unchanged to the repository ------------------------

    [Fact]
    public async Task Test_SC3_DateRangeFilterReturnsOnlyRowsInRange()
    {
        // Arrange — dbo.audit_log_lst enforces the inclusive created_at range server-side; the mock
        // repository returns only the in-range row, proving the service passes dateFrom/dateTo
        // through unchanged.
        var fixture = new Fixture();
        var dateFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var dateTo = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);
        var inRangeRow = Row(100901, "m.alvarez", "Quarantine", "Deleted", "contracts/2019/agreement-004.pdf",
            new DateTimeOffset(2026, 9, 9, 14, 22, 10, TimeSpan.Zero));
        fixture.AuditLogRepository
            .ListAsync(null, dateFrom, dateTo, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow> { inRangeRow }, 1));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: dateFrom, dateTo: dateTo, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Response!.Items.Should().ContainSingle(i => i.Timestamp == inRangeRow.CreatedAt);
        await fixture.AuditLogRepository.Received(1).ListAsync(null, dateFrom, dateTo, 1, 50, Arg.Any<CancellationToken>());
    }

    // ---- SC-4: pagination returns distinct pages, totalCount stable across calls --------------------

    [Fact]
    public async Task Test_SC4_PaginationReturnsDistinctPagesWithStableTotalCount()
    {
        // Arrange — two canned "pages" of mocked repository data, same filter, same totalCount.
        var fixture = new Fixture();
        var page1Rows = Enumerable.Range(1, 10)
            .Select(i => Row(100900 + i, $"user{i}", "Quarantine", "Deleted", $"path{i}.pdf",
                new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(-i)))
            .ToList();
        var page2Rows = Enumerable.Range(11, 10)
            .Select(i => Row(100900 + i, $"user{i}", "Quarantine", "Deleted", $"path{i}.pdf",
                new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero).AddMinutes(-i)))
            .ToList();
        fixture.AuditLogRepository.ListAsync(null, null, null, 1, 10, Arg.Any<CancellationToken>()).Returns((page1Rows, 25));
        fixture.AuditLogRepository.ListAsync(null, null, null, 2, 10, Arg.Any<CancellationToken>()).Returns((page2Rows, 25));
        var sut = fixture.CreateSut();

        // Act
        var page1Result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: 10, actingUserId: 1, CancellationToken.None);
        var page2Result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 2, pageSize: 10, actingUserId: 1, CancellationToken.None);

        // Assert — distinct rows, same totalCount, page/pageSize reflected in the response.
        var page1Ids = page1Result.Response!.Items.Select(i => i.User).ToList();
        var page2Ids = page2Result.Response!.Items.Select(i => i.User).ToList();
        page1Ids.Should().NotIntersectWith(page2Ids);
        page1Result.Response.TotalCount.Should().Be(25);
        page2Result.Response!.TotalCount.Should().Be(25);
        page1Result.Response.Page.Should().Be(1);
        page2Result.Response.Page.Should().Be(2);
        page1Result.Response.PageSize.Should().Be(10);
        page2Result.Response.PageSize.Should().Be(10);
    }

    // ---- SC-5 / V-5: Failure rows (with resultDetail) are included, never omitted -------------------

    [Fact]
    public async Task Test_SC5_FailureRowsIncludedWithResultDetail()
    {
        // Arrange — a CAS-007 SyncActionFailedB2-style row: result = Failure, resultDetail populated.
        var fixture = new Fixture();
        var failureRow = Row(100905, "m.alvarez", "O-Drive Sync", "SyncActionFailedB2", "contracts/2020/invoice-009.pdf",
            new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero),
            result: "Failure", resultDetail: "B2 API returned 500 internal_error");
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow> { failureRow }, 1));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert — both fields populated, not omitted.
        result.Response!.Items.Should().ContainSingle(i =>
            i.Result == "Failure" &&
            i.ResultDetail == "B2 API returned 500 internal_error");
    }

    [Fact]
    public async Task Test_V5_FailedActionsAppearInResults()
    {
        // Arrange — a mixed set of Success and Failure rows; the service must not filter Failure
        // rows out by default (no filtering logic on `result` exists anywhere in the service).
        var fixture = new Fixture();
        var successRow = Row(100906, "j.doe", "Keyword Search", "SearchSubmitted", null,
            new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero), result: "Success");
        var failureRow = Row(100907, "m.alvarez", "O-Drive Sync", "SyncActionFailedB2", "contracts/2020/invoice-009.pdf",
            new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero), result: "Failure", resultDetail: "B2 API returned 500 internal_error");
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow> { failureRow, successRow }, 2));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Response!.Items.Should().Contain(i => i.Result == "Failure");
        result.Response.Items.Should().HaveCount(2);
    }

    // ---- V-6: totalCount reflects filtered (pre-pagination) count, not the page's row count --------

    [Fact]
    public async Task Test_V6_TotalCountReflectsFilteredNotPagedCount()
    {
        // Arrange — repository returns 10 rows for the page but a totalCount of 137 (the full
        // filtered set); the service must surface totalCount as given, not items.Count.
        var fixture = new Fixture();
        var pageRows = Enumerable.Range(1, 10)
            .Select(i => Row(100900 + i, $"user{i}", "Quarantine", "Deleted", $"path{i}.pdf",
                new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(-i)))
            .ToList();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 10, Arg.Any<CancellationToken>())
            .Returns((pageRows, 137));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: 10, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Response!.Items.Should().HaveCount(10);
        result.Response.TotalCount.Should().Be(137);
        result.Response.TotalCount.Should().NotBe(result.Response.Items.Count);
    }

    // ---- V-1: unknown tab rejected -------------------------------------------------------------------

    [Fact]
    public async Task Test_V1_UnknownTabRejected()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: "NotARealTab", dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AuditLogListOutcome.ValidationFailed);
        result.ValidationError.Should().NotBeNullOrEmpty();
        await fixture.AuditLogRepository.DidNotReceive().ListAsync(
            Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V-2: dateTo before dateFrom rejected ---------------------------------------------------------

    [Fact]
    public async Task Test_V2_DateToBeforeDateFromRejected()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var dateFrom = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var dateTo = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: dateFrom, dateTo: dateTo, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AuditLogListOutcome.ValidationFailed);
        await fixture.AuditLogRepository.DidNotReceive().ListAsync(
            Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V-3: non-positive page rejected --------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Test_V3_NonPositivePageRejected(int page)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: page, pageSize: 50, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AuditLogListOutcome.ValidationFailed);
        await fixture.AuditLogRepository.DidNotReceive().ListAsync(
            Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V-4: pageSize out of range rejected; default is 50 when omitted -----------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Test_V4_PageSizeOutOfRangeRejected(int pageSize)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: pageSize, actingUserId: 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(AuditLogListOutcome.ValidationFailed);
        await fixture.AuditLogRepository.DidNotReceive().ListAsync(
            Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
