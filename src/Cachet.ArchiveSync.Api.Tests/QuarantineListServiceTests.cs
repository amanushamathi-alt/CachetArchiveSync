using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="QuarantineListService"/> (CAS-008 §6 <c>GET /quarantine</c>, FR-14) —
/// the spec's §13 Test Plan "QuarantineListServiceTests" assignment: SC-1, V-7. The actual
/// <c>status = 'Active'</c> filter is enforced by <c>dbo.quarantine_entry_lst</c>/
/// <see cref="Repositories.QuarantineEntryRepository.ListActiveAsync"/> (a real-database concern);
/// this class proves the service layer is a faithful passthrough of whatever the repository
/// returns — it neither re-includes excluded rows nor drops rows the repository already filtered
/// for it — while <see cref="Cachet.ArchiveSync.Api.IntegrationTests.QuarantineControllerTests"/>
/// proves the same criteria at the HTTP level.
/// </summary>
public sealed class QuarantineListServiceTests
{
    private sealed class Fixture
    {
        public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
        public ILogger<QuarantineListService> Logger { get; } = Substitute.For<ILogger<QuarantineListService>>();

        public QuarantineListService CreateSut() => new(QuarantineEntryRepository, Logger);
    }

    private static QuarantineEntryListRow ActiveRow(int quarantineEntryId, string name, string path) => new()
    {
        QuarantineEntryId = quarantineEntryId,
        FileIndexId = quarantineEntryId + 500_000,
        Name = name,
        Path = path,
        QuarantinedOn = new DateTimeOffset(2026, 9, 9, 14, 10, 0, TimeSpan.Zero),
        Status = "Flagged",
        SourceTab = "Keyword Search"
    };

    // ---- SC-1: GET /quarantine returns every Active row, correctly shaped -------------------------

    [Fact]
    public async Task Test_SC1_ListReturnsOnlyActiveEntries()
    {
        // Arrange — the repository (dbo.quarantine_entry_lst) is the layer that enforces
        // status = 'Active'; here it returns exactly the two Active rows it is contracted to.
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository.ListActivePagedAsync(1, 25, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryListPage
        {
            TotalCount = 2,
            Rows = new List<QuarantineEntryListRow>
            {
                ActiveRow(100801, "agreement-004.pdf", "contracts/2019/agreement-004.pdf"),
                ActiveRow(100802, "invoice-009.pdf", "contracts/2020/invoice-009.pdf")
            }
        });
        var sut = fixture.CreateSut();

        // Act
        var response = await sut.GetActiveAsync(actingUserId: 1, page: 1, pageSize: 25, CancellationToken.None);

        // Assert — every returned row is present, correctly shaped per §6.
        response.Items.Should().HaveCount(2);
        response.Items.Should().ContainSingle(i =>
            i.FileId == 100801 &&
            i.Name == "agreement-004.pdf" &&
            i.Path == "contracts/2019/agreement-004.pdf" &&
            i.Status == "Flagged" &&
            i.QuarantinedOn == new DateTimeOffset(2026, 9, 9, 14, 10, 0, TimeSpan.Zero));
        response.Items.Should().ContainSingle(i => i.FileId == 100802);
    }

    [Fact]
    public async Task Test_SC1_EmptyQueueReturnsEmptyItemsArray()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.QuarantineEntryRepository.ListActivePagedAsync(1, 25, Arg.Any<CancellationToken>())
            .Returns(new QuarantineEntryListPage { TotalCount = 0, Rows = new List<QuarantineEntryListRow>() });
        var sut = fixture.CreateSut();

        // Act
        var response = await sut.GetActiveAsync(actingUserId: 1, page: 1, pageSize: 25, CancellationToken.None);

        // Assert
        response.Items.Should().BeEmpty();
    }

    // ---- V-7: GET /quarantine never re-includes or invents a non-Active row ------------------------

    [Fact]
    public async Task Test_V7_ListExcludesNonActiveEntries()
    {
        // Arrange — the repository, by contract, returns only Active rows (Released/Deleted rows
        // are never handed to this service). Prove the service does not perform any independent
        // enrichment/union that could reintroduce an excluded row — its item count and identities
        // must equal exactly what the repository returned, no more, no less.
        var fixture = new Fixture();
        var onlyActiveRow = ActiveRow(100801, "agreement-004.pdf", "contracts/2019/agreement-004.pdf");
        fixture.QuarantineEntryRepository.ListActivePagedAsync(1, 25, Arg.Any<CancellationToken>())
            .Returns(new QuarantineEntryListPage { TotalCount = 1, Rows = new List<QuarantineEntryListRow> { onlyActiveRow } });
        var sut = fixture.CreateSut();

        // Act
        var response = await sut.GetActiveAsync(actingUserId: 1, page: 1, pageSize: 25, CancellationToken.None);

        // Assert
        response.Items.Should().ContainSingle();
        response.Items.Single().FileId.Should().Be(100801);
        await fixture.QuarantineEntryRepository.Received(1).ListActivePagedAsync(1, 25, Arg.Any<CancellationToken>());
    }

    // ---- CAS-050 SC-8 (service level): paging passes through; envelope carries TotalCount/Page/PageSize; SourceTab mapped

    [Fact]
    public async Task Test_SC8_ListService_PassesPagingAndMapsEnvelopeAndSourceTab()
    {
        // Arrange
        var fixture = new Fixture();
        var filesRow = ActiveRow(100026, "a.pdf", "Ops/a.pdf") with { SourceTab = "Files", Status = "Informational" };
        var searchRow = ActiveRow(100027, "b.pdf", "Ops/b.pdf");
        fixture.QuarantineEntryRepository.ListActivePagedAsync(2, 25, Arg.Any<CancellationToken>())
            .Returns(new QuarantineEntryListPage { TotalCount = 60, Rows = new List<QuarantineEntryListRow> { filesRow, searchRow } });
        var sut = fixture.CreateSut();

        // Act
        var response = await sut.GetActiveAsync(actingUserId: 1, page: 2, pageSize: 25, CancellationToken.None);

        // Assert
        response.TotalCount.Should().Be(60);
        response.Page.Should().Be(2);
        response.PageSize.Should().Be(25);
        response.Items.Select(i => (i.FileId, i.SourceTab)).Should().Equal((100026, "Files"), (100027, "Keyword Search"));
        await fixture.QuarantineEntryRepository.Received(1).ListActivePagedAsync(2, 25, Arg.Any<CancellationToken>());
    }
}
