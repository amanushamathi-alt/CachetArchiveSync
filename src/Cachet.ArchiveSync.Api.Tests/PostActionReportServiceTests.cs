using System.Linq;
using Cachet.ArchiveSync.Api.Reporting;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PdfSharp.Pdf.IO;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="PostActionReportService"/> (CAS-010 §3/§6.2, FR-26; CAS-015 §2/§5/§8/§9).
/// The spec's §13 Test Plan "PostActionReportServiceTests" assignment: SC-2 (both formats), V3, V5,
/// V6, V8. Sources rows from <see cref="IAuditLogRepository.ListAsync"/> (CAS-009), so this class
/// mocks that repository, not a second parallel action-history store. CAS-015 removed the
/// <c>IReportRepository</c>/<c>IConfiguration</c> constructor dependencies entirely (§5).
/// </summary>
public sealed class PostActionReportServiceTests
{
    private sealed class Fixture
    {
        public IAuditLogRepository AuditLogRepository { get; } = Substitute.For<IAuditLogRepository>();
        public IReportDocumentWriter ReportDocumentWriter { get; set; } = Substitute.For<IReportDocumentWriter>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<PostActionReportService> Logger { get; } = Substitute.For<ILogger<PostActionReportService>>();

        public PostActionReportService CreateSut() => new(
            AuditLogRepository, ReportDocumentWriter, AuditLogService, Logger);
    }

    private static AuditLogRow Row(int id, string action, string result, string? filePath = "contracts/2019/agreement-004.pdf") => new()
    {
        AuditLogId = id,
        Username = "m.alvarez",
        TabModule = "Quarantine",
        Action = action,
        FilePath = filePath,
        CreatedAt = DateTimeOffset.UtcNow,
        Result = result
    };

    // ---- SC-2: POST /reports/post-action, both "pdf" and "xlsx" -> analogous parseable results ----

    [Theory]
    [InlineData("pdf", "application/pdf")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task Test_SC2_PostActionPdfAndXlsx_ReturnParseableBodies(string format, string expectedContentType)
    {
        // Arrange — real writer, so both formats' bytes are provably parseable by the library that
        // would later read them, not just non-empty byte arrays.
        var fixture = new Fixture { ReportDocumentWriter = new ReportDocumentWriter(new PdfSharpReportWriter(), new ClosedXmlReportWriter()) };
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow> { Row(1, nameof(AuditAction.Deleted), nameof(AuditResult.Success)) }, 1));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = format }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        result.ContentType.Should().Be(expectedContentType);
        result.DownloadFileName.Should().MatchRegex($@"^post-action-report-\d{{14}}\.{format}$");
        result.FileBytes.Should().NotBeNullOrEmpty();

        if (format == "pdf")
        {
            Action openPdf = () => PdfReader.Open(new MemoryStream(result.FileBytes!), PdfDocumentOpenMode.Import).Dispose();
            openPdf.Should().NotThrow();
        }
        else
        {
            Action openXlsx = () => new XLWorkbook(new MemoryStream(result.FileBytes!)).Dispose();
            openXlsx.Should().NotThrow();
        }
    }

    // ---- V-3: read-only/auth actions never appear as rows, unchanged -------------------------------

    [Fact]
    public async Task Test_V3_ReadOnlyAndAuthActionsExcludedFromPostActionReport()
    {
        // Arrange — every non-allow-listed AuditAction §6.2 names explicitly, plus one allow-listed
        // row that must survive the filter.
        var fixture = new Fixture();
        fixture.ReportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        var excludedRows = new List<AuditLogRow>
        {
            Row(1, nameof(AuditAction.Login), nameof(AuditResult.Success)),
            Row(2, nameof(AuditAction.LoginFailed), nameof(AuditResult.Failure)),
            Row(3, nameof(AuditAction.Logout), nameof(AuditResult.Success)),
            Row(4, nameof(AuditAction.CrossCheckExcluded), nameof(AuditResult.Success)),
            Row(5, nameof(AuditAction.PreActionReportGenerated), nameof(AuditResult.Success)),
            Row(6, nameof(AuditAction.PostActionReportGenerated), nameof(AuditResult.Success))
        };
        var allowListedRow = Row(7, nameof(AuditAction.Deleted), nameof(AuditResult.Success));
        var allRows = excludedRows.Append(allowListedRow).ToList();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((allRows, allRows.Count));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — proven via the document actually handed to the writer, since the result no
        // longer exposes rows[] directly.
        result.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        await fixture.ReportDocumentWriter.Received(1).WritePdfAsync(
            Arg.Is<ReportDocument>(d => d.Rows.Count == 1 && d.Rows[0][2] == "Deleted Permanently"),
            Arg.Any<CancellationToken>());
    }

    // ---- V-5: failed audit rows are included, never silently dropped, unchanged --------------------

    [Fact]
    public async Task Test_V5_FailedActionsAppearInPostActionReport()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.ReportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        var failedRow = Row(1, nameof(AuditAction.Deleted), nameof(AuditResult.Failure));
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow> { failedRow }, 1));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        await fixture.ReportDocumentWriter.Received(1).WritePdfAsync(
            Arg.Is<ReportDocument>(d => d.Rows.Count == 1 && d.Rows[0][5] == "Failure" && d.Rows[0][2] == "Deleted Permanently"),
            Arg.Any<CancellationToken>());
    }

    // ---- V-6: the 100,000-row cap is reached exactly -> fail loud rather than truncate, unchanged --

    [Fact]
    public async Task Test_V6_AuditLogCapReachedFailsLoudRatherThanTruncating()
    {
        // Arrange — exactly AuditLogReadCap (100,000) rows returned is ambiguous (cannot tell true
        // total from a truncation), so the request must fail rather than silently return a partial,
        // incomplete defensible record (§9 V6).
        var fixture = new Fixture();
        var cappedRows = Enumerable.Range(1, 100_000)
            .Select(i => Row(i, nameof(AuditAction.Deleted), nameof(AuditResult.Success)))
            .ToList();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((cappedRows, 100_000));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ReportGenerationOutcome.GenerationFailed);
        result.FileBytes.Should().BeNull();
        await fixture.ReportDocumentWriter.DidNotReceive().WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V6_BelowCapSucceedsNormally()
    {
        // Arrange — 99,999 rows (one below the cap) is unambiguous and must succeed, proving the
        // guard is an exact-equality check, not an off-by-one "at or above the cap" check.
        var fixture = new Fixture();
        fixture.ReportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        var almostCappedRows = Enumerable.Range(1, 99_999)
            .Select(i => Row(i, nameof(AuditAction.Deleted), nameof(AuditResult.Success)))
            .ToList();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((almostCappedRows, 99_999));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ReportGenerationOutcome.Generated);
        await fixture.ReportDocumentWriter.Received(1).WritePdfAsync(
            Arg.Is<ReportDocument>(d => d.Rows.Count == 99_999), Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 SC-2: GET /reports/post-action/preview -> generatedAt/generatedBy/rows[] shape ----
    // matching CAS-032 §6.2 exactly, including null fileName/filePath for task-level rows. -----------

    [Fact]
    public async Task Test_SC2_PostActionPreview_ReturnsExpectedShape()
    {
        // Arrange — one file-level row and one task-level row (no associated file).
        var fixture = new Fixture();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>
            {
                Row(1, nameof(AuditAction.Deleted), nameof(AuditResult.Success)),
                Row(2, nameof(AuditAction.KeywordSearchSubmitted), nameof(AuditResult.Success), filePath: null)
            }, 2));
        var sut = fixture.CreateSut();
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — CAS-038 §6 response shape.
        result.Outcome.Should().Be(ReportPreviewOutcome.Generated);
        var response = result.Response!;
        response.GeneratedBy.Should().Be("m.alvarez");
        response.GeneratedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        response.Rows.Should().HaveCount(2);
        response.Rows[0].FileName.Should().Be("agreement-004.pdf");
        response.Rows[0].FilePath.Should().Be("contracts/2019/agreement-004.pdf");
        response.Rows[0].ActionTaken.Should().Be("Deleted Permanently");
        response.Rows[1].FileName.Should().BeNull("task-level actions have no single associated file");
        response.Rows[1].FilePath.Should().BeNull();
        response.Rows[1].ActionTaken.Should().Be("Searched");
    }

    // ---- CAS-038 V-3: preview applies the §6.2 allow-list and includes failed rows, same as -------
    // GenerateAsync (CAS-015 V3/V5). --------------------------------------------------------------

    [Fact]
    public async Task Test_V3_PostActionPreviewAppliesAllowListAndIncludesFailures()
    {
        // Arrange — every non-allow-listed AuditAction §6.2 names explicitly, plus one allow-listed
        // *failed* row that must survive both the filter and the "never drop failures" rule (V5).
        var fixture = new Fixture();
        var excludedRows = new List<AuditLogRow>
        {
            Row(1, nameof(AuditAction.Login), nameof(AuditResult.Success)),
            Row(2, nameof(AuditAction.LoginFailed), nameof(AuditResult.Failure)),
            Row(3, nameof(AuditAction.Logout), nameof(AuditResult.Success)),
            Row(4, nameof(AuditAction.CrossCheckExcluded), nameof(AuditResult.Success)),
            Row(5, nameof(AuditAction.PreActionReportGenerated), nameof(AuditResult.Success)),
            Row(6, nameof(AuditAction.PostActionReportGenerated), nameof(AuditResult.Success))
        };
        var allowListedFailureRow = Row(7, nameof(AuditAction.Deleted), nameof(AuditResult.Failure));
        var allRows = excludedRows.Append(allowListedFailureRow).ToList();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((allRows, allRows.Count));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ReportPreviewOutcome.Generated);
        result.Response!.Rows.Should().ContainSingle();
        result.Response.Rows[0].ActionTaken.Should().Be("Deleted Permanently");
        result.Response.Rows[0].Result.Should().Be("Failure", "failed rows are included, never dropped (CAS-015 V5)");
    }

    // ---- CAS-038 V-5: the 100,000-row cap is reached exactly -> fail loud rather than truncate, ----
    // same rule as CAS-015 V6 for the downloaded file. ------------------------------------------------

    [Fact]
    public async Task Test_V5_PostActionPreviewCapReachedFailsLoud()
    {
        // Arrange
        var fixture = new Fixture();
        var cappedRows = Enumerable.Range(1, 100_000)
            .Select(i => Row(i, nameof(AuditAction.Deleted), nameof(AuditResult.Success)))
            .ToList();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((cappedRows, 100_000));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.PreviewAsync(actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — fails loud rather than silently truncating; no partial rows are exposed either.
        result.Outcome.Should().Be(ReportPreviewOutcome.GenerationFailed);
        result.Response.Should().BeNull();
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 SC-3/V-2: GenerateAsync and PreviewAsync produce identical rows for the same -----
    // underlying data — proven via the shared BuildRowsAsync method, not parallel logic. -------------

    [Fact]
    public async Task Test_SC3_PreviewAndGenerateAsyncProduceIdenticalRows()
    {
        // Arrange — one file-level success row and one task-level (null path) failure row, so the
        // comparison covers both nullability and the Timestamp string-formatting boundary between
        // the rendered document (ToString("u")) and the preview's DateTimeOffset field.
        var fixture = new Fixture();
        var rows = new List<AuditLogRow>
        {
            Row(1, nameof(AuditAction.Deleted), nameof(AuditResult.Success)),
            Row(2, nameof(AuditAction.QuarantineAdd), nameof(AuditResult.Failure), filePath: null)
        };
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((rows, rows.Count));
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
        previewResult.Outcome.Should().Be(ReportPreviewOutcome.Generated);
        capturedDocument.Should().NotBeNull();
        var generatedRows = capturedDocument!.Rows
            .Select(r => (FileName: r[0], FilePath: r[1], ActionTaken: r[2], SourceTab: r[3], Timestamp: r[4], Result: r[5]))
            .ToList();
        var previewRows = previewResult.Response!.Rows
            .Select(r => (FileName: r.FileName ?? string.Empty, FilePath: r.FilePath ?? string.Empty, ActionTaken: r.ActionTaken, SourceTab: r.SourceTab, Timestamp: r.Timestamp.ToString("u"), Result: r.Result))
            .ToList();
        previewRows.Should().Equal(generatedRows);
    }

    // ---- V-8: no file is ever written to disk — the writer's signature carries no file-path -------
    // parameter to write through. ----------------------------------------------------------------

    [Fact]
    public async Task Test_V8_NoFileWrittenToDiskAfterResponse()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>(), 0));
        fixture.ReportDocumentWriter.WriteXlsxAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 9, 8, 7 });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "xlsx" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.FileBytes.Should().Equal(new byte[] { 9, 8, 7 });
        var writeXlsxParameterTypes = typeof(IReportDocumentWriter)
            .GetMethod(nameof(IReportDocumentWriter.WriteXlsxAsync))!
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();
        writeXlsxParameterTypes.Should().Equal(
            new[] { typeof(ReportDocument), typeof(CancellationToken) },
            "IReportDocumentWriter.WriteXlsxAsync must take only (document, ct) — no file-path parameter (CAS-015 §9 V8)");
    }
}
