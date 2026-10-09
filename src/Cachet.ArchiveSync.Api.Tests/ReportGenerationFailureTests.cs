using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-010 §7 V2, carried forward unchanged by CAS-015 §9 — any exception from
/// <see cref="IReportDocumentWriter"/> (PdfSharp/ClosedXML write failure, out-of-memory, malformed
/// input) during either POST endpoint is caught and mapped to
/// <see cref="ReportGenerationOutcome.GenerationFailed"/>, never allowed to bubble as an unhandled
/// 500 with a stack trace. Matches this spec's §13 Test Plan "ReportGenerationFailureTests"
/// assignment. CAS-015 removed the <c>IReportRepository</c>/<c>IConfiguration</c> constructor
/// dependencies and the writer's file-path parameter — the try/catch shape itself is unchanged, now
/// wrapping a render-to-bytes call instead of a render-to-file call.
/// </summary>
public sealed class ReportGenerationFailureTests
{
    // ---- V-2: a PDF write failure during Pre-Action never bubbles as an unhandled 500 --------------

    [Fact]
    public async Task Test_V2_PdfWriteFailureReturnsReportGenerationFailed()
    {
        // Arrange
        var searchResultRepository = Substitute.For<ISearchResultRepository>();
        var quarantineEntryRepository = Substitute.For<IQuarantineEntryRepository>();
        var reportDocumentWriter = Substitute.For<IReportDocumentWriter>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var logger = Substitute.For<ILogger<PreActionReportService>>();

        searchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        quarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        reportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>())
            .Returns<Task<byte[]>>(_ => throw new UnauthorizedAccessException("Permission denied."));

        var sut = new PreActionReportService(
            searchResultRepository, quarantineEntryRepository, reportDocumentWriter, auditLogService, logger);

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "pdf" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert — mapped to GenerationFailed, not an unhandled exception; no audit row is written
        // for a report that never fully rendered (§9 V2, §11).
        result.Outcome.Should().Be(ReportGenerationOutcome.GenerationFailed);
        result.FileBytes.Should().BeNull();
        await auditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- V-2: an XLSX write failure during Post-Action never bubbles as an unhandled 500 -----------

    [Fact]
    public async Task Test_V2_XlsxWriteFailureReturnsReportGenerationFailed()
    {
        // Arrange — same scenario against PostActionReportService, on the XLSX write this time, to
        // prove the catch is not narrowly scoped to one report type or one of the two file formats.
        var auditLogRepository = Substitute.For<IAuditLogRepository>();
        var reportDocumentWriter = Substitute.For<IReportDocumentWriter>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var logger = Substitute.For<ILogger<PostActionReportService>>();

        auditLogRepository.ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>(), 0));
        reportDocumentWriter.WriteXlsxAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>())
            .Returns<Task<byte[]>>(_ => throw new IOException("Out of memory during render."));

        var sut = new PostActionReportService(
            auditLogRepository, reportDocumentWriter, auditLogService, logger);

        // Act
        var result = await sut.GenerateAsync(
            new ReportGenerationRequest { Format = "xlsx" }, actingUserId: 1, actingUsername: "m.alvarez", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ReportGenerationOutcome.GenerationFailed);
        result.FileBytes.Should().BeNull();
        await auditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }
}
