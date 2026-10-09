using System.Linq;
using Cachet.ArchiveSync.Api.Reporting;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PdfSharp.Pdf.IO;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="PreActionReportService"/> (CAS-010 §3/§6.1, FR-25; CAS-015 §2/§5/§8/§9).
/// The spec's §13 Test Plan "PreActionReportServiceTests" assignment: SC-1, SC-2 (Pre-Action XLSX
/// companion), V4, V8. CAS-015 removed the <c>IReportRepository</c>/<c>IConfiguration</c>
/// constructor dependencies entirely (§5) — the service now takes only
/// <see cref="ISearchResultRepository"/>, <see cref="IQuarantineEntryRepository"/>,
/// <see cref="IReportDocumentWriter"/>, <see cref="IAuditLogService"/>, and its
/// <see cref="ILogger{TCategoryName}"/>, and returns the rendered file's bytes/content-type/
/// filename directly (no <c>reportId</c>/download URL, no <c>Rows</c> exposed in the result).
/// </summary>
public sealed class PreActionReportServiceTests
{
    private sealed class Fixture
    {
        public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();
        public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
        public IReportDocumentWriter ReportDocumentWriter { get; set; } = Substitute.For<IReportDocumentWriter>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<PreActionReportService> Logger { get; } = Substitute.For<ILogger<PreActionReportService>>();

        public PreActionReportService CreateSut() => new(
            SearchResultRepository, QuarantineEntryRepository, ReportDocumentWriter, AuditLogService, Logger);
    }

    private static SearchResultRow SearchRow(string matchType, string matchStatus, string fileName, string path) => new()
    {
        SearchResultId = 1,
        TaskQueueId = 100234,
        FileIndexId = 200501,
        MatchType = matchType,
        MatchStatus = matchStatus,
        CreatedAt = DateTimeOffset.UtcNow,
        FileName = fileName,
        RelativePath = path,
        FileSizeBytes = 100,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static QuarantineEntryListRow QuarantineRow(string name, string path, string status) => new()
    {
        QuarantineEntryId = 100801,
        FileIndexId = 600801,
        Name = name,
        Path = path,
        QuarantinedOn = DateTimeOffset.UtcNow,
        Status = status,
        SourceTab = "Keyword Search"
    };

    // ---- SC-1: POST /reports/pre-action, format "pdf" -> 200, application/pdf, parseable, in the
    // same response — no reportId, no separate GET call needed. ------------------------------------

    [Fact]
    public async Task Test_SC1_PreActionPdf_ReturnsParseablePdfInSameResponse()
    {
        // Arrange — the real writer (not mocked), so the returned bytes are provably a valid PDF
        // that PdfSharp can re-open, per SC-1's own wording, not just a non-empty byte array.
        var fixture = new Fixture { ReportDocumentWriter = new ReportDocumentWriter(new PdfSharpReportWriter(), new ClosedXmlReportWriter()) };
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>
        {
            SearchRow("Filename", "Flagged", "agreement-004.pdf", "contracts/2019/agreement-004.pdf")
        });
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — generated and returned in this one call; PreActionReportResult carries no
        // reportId/download-URL field at all (CAS-015 §2/§8).
        result.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        result.ContentType.Should().Be("application/pdf");
        result.FileBytes.Should().NotBeNullOrEmpty();
        result.DownloadFileName.Should().MatchRegex(@"^pre-action-report-\d{14}\.pdf$");
        Action openPdf = () => PdfReader.Open(new MemoryStream(result.FileBytes!), PdfDocumentOpenMode.Import).Dispose();
        openPdf.Should().NotThrow();
    }

    // ---- SC-2: POST /reports/pre-action, format "xlsx" -> analogous parseable result --------------

    [Fact]
    public async Task Test_SC2_PreActionXlsx_ReturnsParseableXlsx()
    {
        // Arrange
        var fixture = new Fixture { ReportDocumentWriter = new ReportDocumentWriter(new PdfSharpReportWriter(), new ClosedXmlReportWriter()) };
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>
        {
            SearchRow("Content", "Needs Review", "invoice-009.pdf", "contracts/2020/invoice-009.pdf")
        });
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>
        {
            QuarantineRow("policy.docx", "hr/policy.docx", "Flagged")
        });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "xlsx" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        result.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        result.DownloadFileName.Should().MatchRegex(@"^pre-action-report-\d{14}\.xlsx$");
        Action openXlsx = () => new XLWorkbook(new MemoryStream(result.FileBytes!)).Dispose();
        openXlsx.Should().NotThrow();
    }

    // ---- V-4: excludes files where dbo.file_index.is_active = 0 (Permanently Deleted), unchanged --

    [Fact]
    public async Task Test_V4_SoftDeletedFilesExcludedFromPreActionReport()
    {
        // Arrange — ISearchResultRepository.ListAllActiveAsync (§5/§6) is the layer contracted to
        // filter is_active = 1; this proves the service is a faithful passthrough of whatever the
        // repository returns — it neither re-includes an excluded file via some independent query
        // nor drops an active row the repository already returned.
        var fixture = new Fixture();
        var onlyActiveRow = SearchRow("Filename", "Flagged", "agreement-004.pdf", "contracts/2019/agreement-004.pdf");
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow> { onlyActiveRow });
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        fixture.ReportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — the single active row survives, proven via the document actually handed to the
        // writer (the result no longer exposes rows[] directly — CAS-015 §2 removed that field).
        result.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        await fixture.ReportDocumentWriter.Received(1).WritePdfAsync(
            Arg.Is<ReportDocument>(d => d.Rows.Count == 1 && d.Rows[0][0] == "agreement-004.pdf"),
            Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.Received(1).ListAllActiveAsync(Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 SC-1: GET /reports/pre-action/preview -> generatedAt/generatedBy/rows[] shape ----
    // matching CAS-032 §6.2 exactly, including the zero-row case. ------------------------------------

    [Fact]
    public async Task Test_SC1_PreActionPreview_ReturnsExpectedShape()
    {
        // Arrange — one search row (mapped via panel) + one quarantine row, proving both sources
        // surface in the same rows[] with the exact field names CAS-032's ReportModal.razor expects.
        var fixture = new Fixture();
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>
        {
            SearchRow("Filename", "Flagged", "agreement-004.pdf", "contracts/2019/agreement-004.pdf")
        });
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>
        {
            QuarantineRow("policy.docx", "hr/policy.docx", "Flagged")
        });
        var sut = fixture.CreateSut();
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — CAS-038 §6 response shape: generatedAt/generatedBy/rows[], no reportId/download URL.
        result.GeneratedBy.Should().Be("m.alvarez");
        result.GeneratedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        result.Rows.Should().HaveCount(2);
        result.Rows[0].FileName.Should().Be("agreement-004.pdf");
        result.Rows[0].FilePath.Should().Be("contracts/2019/agreement-004.pdf");
        result.Rows[0].Panel.Should().Be("Filename Matches");
        result.Rows[0].Status.Should().Be("Flagged");
        result.Rows[1].FileName.Should().Be("policy.docx");
        result.Rows[1].FilePath.Should().Be("hr/policy.docx");
        result.Rows[1].Panel.Should().Be("Quarantine");
        result.Rows[1].Status.Should().Be("Flagged");

        // Zero-row case (§10 SC-1's explicit "including the zero-row case" clause).
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        var emptyResult = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);
        emptyResult.Rows.Should().BeEmpty();
    }

    // ---- CAS-038 V-4: preview excludes soft-deleted (is_active = 0) files, same as GenerateAsync ----

    [Fact]
    public async Task Test_V4_PreActionPreviewExcludesSoftDeletedFiles()
    {
        // Arrange — mirrors Test_V4_SoftDeletedFilesExcludedFromPreActionReport for the new preview
        // path: ISearchResultRepository.ListAllActiveAsync (§5/§6) is the layer contracted to filter
        // is_active = 1; PreviewAsync must be a faithful passthrough, never an independent query.
        var fixture = new Fixture();
        var onlyActiveRow = SearchRow("Filename", "Flagged", "agreement-004.pdf", "contracts/2019/agreement-004.pdf");
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow> { onlyActiveRow });
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — only the single active row the repository returned survives into rows[]; a
        // permanently-deleted (is_active = 0) file was never handed to this method to begin with,
        // since the repository itself is the filtering boundary.
        result.Rows.Should().ContainSingle();
        result.Rows[0].FileName.Should().Be("agreement-004.pdf");
        await fixture.SearchResultRepository.Received(1).ListAllActiveAsync(Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 SC-3/V-2: GenerateAsync and PreviewAsync produce identical rows for the same -----
    // underlying data — proven via the shared BuildRowsAsync method, not parallel logic. -------------

    [Fact]
    public async Task Test_SC3_PreviewAndGenerateAsyncProduceIdenticalRows()
    {
        // Arrange — capture the ReportDocument actually handed to the writer by GenerateAsync (which
        // no longer exposes rows[] directly — CAS-015 §2) and compare it against PreviewAsync's
        // rows[] for the exact same repository data.
        var fixture = new Fixture();
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>
        {
            SearchRow("Filename", "Flagged", "agreement-004.pdf", "contracts/2019/agreement-004.pdf"),
            SearchRow("Content", "Needs Review", "invoice-009.pdf", "contracts/2020/invoice-009.pdf")
        });
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>
        {
            QuarantineRow("policy.docx", "hr/policy.docx", "Flagged")
        });
        ReportDocument? capturedDocument = null;
        fixture.ReportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 })
            .AndDoes(ci => capturedDocument = ci.Arg<ReportDocument>());
        var sut = fixture.CreateSut();

        // Act
        var generateResult = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);
        var previewResult = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        generateResult.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        capturedDocument.Should().NotBeNull();
        var generatedRows = capturedDocument!.Rows.Select(r => (r[0], r[1], r[2], r[3])).ToList();
        var previewRows = previewResult.Rows.Select(r => (r.FileName, r.FilePath, r.Panel, r.Status)).ToList();
        previewRows.Should().Equal(generatedRows);
    }

    // ---- V-8: no file is ever written to disk — the rendering path returns bytes only, and the ----
    // writer's own signature carries no file-path parameter to write through. ------------------------

    [Fact]
    public async Task Test_V8_NoFileWrittenToDiskAfterResponse()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        fixture.ReportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — the byte[] the writer returned is exactly what the result carries (a pure
        // in-memory hand-off), and IReportDocumentWriter's signature has no file-path parameter to
        // write through at all — a compile-time guarantee reinforced here at runtime.
        result.FileBytes.Should().Equal(new byte[] { 1, 2, 3 });
        var writePdfParameterTypes = typeof(IReportDocumentWriter)
            .GetMethod(nameof(IReportDocumentWriter.WritePdfAsync))!
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();
        writePdfParameterTypes.Should().Equal(
            new[] { typeof(ReportDocument), typeof(CancellationToken) },
            "IReportDocumentWriter.WritePdfAsync must take only (document, ct) — no file-path parameter (CAS-015 §9 V8)");
    }

    // ---- CAS-047 SC-10: new-mode results labelled by match_type alone ----

    [Fact]
    public async Task Test_SC10_ReportsLabelNewModeResultsByMatchType()
    {
        // Arrange — Filename mode stores match_type 'Filename'/'Flagged'; Keyword mode stores 'Content'/'Needs Review'.
        var fixture = new Fixture();
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>
        {
            SearchRow("Filename", "Flagged", "agreement-004.pdf", "contracts/agreement-004.pdf"),
            SearchRow("Content", "Needs Review", "notes.docx", "internal/notes.docx")
        });
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Rows.Should().HaveCount(2);
        result.Rows[0].Panel.Should().Be("Filename Matches");
        result.Rows[1].Panel.Should().Be("Content Matches");
    }

    // ---- CAS-050 V10 (report half): a Files-added Quarantine row is listed exactly like a Keyword Search row

    [Fact]
    public async Task Test_V10_FilesAddedQuarantineRow_ListedInPreActionReportLikeKeywordSearchRow()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        fixture.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>
        {
            QuarantineRow("keyword.docx", "hr/keyword.docx", "Flagged"),
            QuarantineRow("files-added.pdf", "Ops/Audit/files-added.pdf", "Informational") with { SourceTab = "Files" }
        });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert - same Quarantine panel, same field mapping; origin makes no difference.
        result.Rows.Should().HaveCount(2);
        result.Rows.Select(r => r.Panel).Should().OnlyContain(p => p == "Quarantine");
        result.Rows[1].FileName.Should().Be("files-added.pdf");
        result.Rows[1].FilePath.Should().Be("Ops/Audit/files-added.pdf");
        result.Rows[1].Status.Should().Be("Informational");
    }
}
