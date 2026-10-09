using System.Linq;
using System.Security.Claims;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Api.Reporting;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Direct, controller-level unit tests for <see cref="ReportsController"/> (CAS-010 §6; CAS-015
/// §2/§5/§8/§9) — the spec's §13 Test Plan "ReportsControllerTests" assignment: V7, SC-5. CAS-015
/// removed the controller's <c>GetPdf</c>/<c>GetXlsx</c> actions and its
/// <c>IReportRepository</c>/<c>_rootPath</c> constructor dependency outright — the controller now
/// depends on only <see cref="IPreActionReportService"/> and <see cref="IPostActionReportService"/>.
/// <see cref="Test_SC4_RemovedGetRoutesReturnNotFound"/> is proven at the HTTP-routing level in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.ReportsControllerTests</c> instead of here, since a
/// removed route cannot be reached by calling a controller action directly — there is no action
/// method left on this class to call.
/// </summary>
public sealed class ReportsControllerTests
{
    private static ReportsController AttachHttpContext(ReportsController controller, int? userId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<ApiBehaviorOptions>(_ => { });
        services.AddSingleton<ProblemDetailsFactory, TestProblemDetailsFactory>();

        var identity = userId is null
            ? new ClaimsIdentity(authenticationType: "Test") // no NameIdentifier claim
            : new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                new Claim(ClaimTypes.Name, "m.alvarez")
            }, "Test");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = new ClaimsPrincipal(identity)
            }
        };
        return controller;
    }

    private static PreActionReportService BuildRealPreActionService(
        ISearchResultRepository searchResultRepository,
        IQuarantineEntryRepository quarantineEntryRepository,
        IReportDocumentWriter reportDocumentWriter,
        IAuditLogService auditLogService) => new(
            searchResultRepository, quarantineEntryRepository, reportDocumentWriter, auditLogService,
            Substitute.For<ILogger<PreActionReportService>>());

    private static PostActionReportService BuildRealPostActionService(
        IAuditLogRepository auditLogRepository,
        IReportDocumentWriter reportDocumentWriter,
        IAuditLogService auditLogService) => new(
            auditLogRepository, reportDocumentWriter, auditLogService,
            Substitute.For<ILogger<PostActionReportService>>());

    // ---- V-7: format must be exactly "pdf" or "xlsx" (case-sensitive) — anything else is a 400 -----
    // validation error, never a silent default to one format. ----------------------------------------

    [Theory]
    [InlineData("docx")]
    [InlineData("PDF")]
    [InlineData("Xlsx")]
    public async Task Test_V7_InvalidFormatReturnsValidationError(string invalidFormat)
    {
        // Arrange — the real PreActionReportService (not a mocked outcome), so this proves the
        // actual §9 V7 case-sensitive check runs, not just that the controller maps a
        // pre-canned ValidationFailed outcome to 400.
        var searchResultRepository = Substitute.For<ISearchResultRepository>();
        var quarantineEntryRepository = Substitute.For<IQuarantineEntryRepository>();
        var reportDocumentWriter = Substitute.For<IReportDocumentWriter>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var preService = BuildRealPreActionService(searchResultRepository, quarantineEntryRepository, reportDocumentWriter, auditLogService);
        var controller = AttachHttpContext(
            new ReportsController(preService, Substitute.For<IPostActionReportService>()), userId: 1);

        // Act
        var result = await controller.PreAction(new ReportGenerationRequest { Format = invalidFormat }, CancellationToken.None);

        // Assert — 400, and no report was rendered or audited for a request that never validated.
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cachet.tracecounts.internal/errors/validation-failed");
        await reportDocumentWriter.DidNotReceive().WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>());
        await reportDocumentWriter.DidNotReceive().WriteXlsxAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>());
        await auditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V7_MissingFormatReturnsValidationError()
    {
        // Arrange — an empty format (the runtime shape of "the field was missing/blank"; the DTO's
        // `required` modifier is a compile-time-only guard against the caller's own C# object
        // initializer, not a runtime guarantee against a missing/empty JSON field on the wire).
        var auditLogRepository = Substitute.For<IAuditLogRepository>();
        var reportDocumentWriter = Substitute.For<IReportDocumentWriter>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var postService = BuildRealPostActionService(auditLogRepository, reportDocumentWriter, auditLogService);
        var controller = AttachHttpContext(
            new ReportsController(Substitute.For<IPreActionReportService>(), postService), userId: 1);

        // Act
        var result = await controller.PostAction(new ReportGenerationRequest { Format = string.Empty }, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cachet.tracecounts.internal/errors/validation-failed");
        await auditLogRepository.DidNotReceive().ListAsync(
            Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await auditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-5: both POST endpoints still write their audit row before the response returns --------

    [Fact]
    public async Task Test_SC5_AuditRowWrittenBeforeResponseReturned()
    {
        var callOrder = new List<string>();

        // ---- POST /reports/pre-action -----------------------------------------------------------
        var searchResultRepository = Substitute.For<ISearchResultRepository>();
        var quarantineEntryRepository = Substitute.For<IQuarantineEntryRepository>();
        var preDocumentWriter = Substitute.For<IReportDocumentWriter>();
        var preAuditLogService = Substitute.For<IAuditLogService>();

        searchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        quarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        preDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 }).AndDoes(_ => callOrder.Add("pre-file-rendered"));
        preAuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.PreActionReportGenerated), Arg.Any<CancellationToken>())
            .Returns(1).AndDoes(_ => callOrder.Add("pre-audit-written"));

        var preService = BuildRealPreActionService(searchResultRepository, quarantineEntryRepository, preDocumentWriter, preAuditLogService);

        // ---- POST /reports/post-action ----------------------------------------------------------
        var postAuditLogRepository = Substitute.For<IAuditLogRepository>();
        var postDocumentWriter = Substitute.For<IReportDocumentWriter>();
        var postAuditLogService = Substitute.For<IAuditLogService>();

        postAuditLogRepository.ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>(), 0));
        postDocumentWriter.WriteXlsxAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 2 }).AndDoes(_ => callOrder.Add("post-file-rendered"));
        postAuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.PostActionReportGenerated), Arg.Any<CancellationToken>())
            .Returns(2).AndDoes(_ => callOrder.Add("post-audit-written"));

        var postService = BuildRealPostActionService(postAuditLogRepository, postDocumentWriter, postAuditLogService);

        var controller = AttachHttpContext(new ReportsController(preService, postService), userId: 1);

        // Act
        var preResult = await controller.PreAction(new ReportGenerationRequest { Format = "pdf" }, CancellationToken.None);
        callOrder.Add("pre-response-returned");
        var postResult = await controller.PostAction(new ReportGenerationRequest { Format = "xlsx" }, CancellationToken.None);
        callOrder.Add("post-response-returned");

        // Assert — the audit row completes before the controller hands back the file response
        // (§11/§12: "before the response returns" — audit-trail requirement unaffected by CAS-015).
        preResult.Should().BeOfType<FileContentResult>();
        postResult.Should().BeOfType<FileContentResult>();
        callOrder.Should().Equal(
            "pre-file-rendered", "pre-audit-written", "pre-response-returned",
            "post-file-rendered", "post-audit-written", "post-response-returned");
    }

    // ---- Auth/failure branch coverage (unmapped to a specific SC/V, kept for §13's ≥85% coverage --
    // target on ReportsController.cs) -----------------------------------------------------------------

    [Fact]
    public async Task Test_PreAction_NoUserIdClaim_Returns401()
    {
        var preActionReportService = Substitute.For<IPreActionReportService>();
        var controller = AttachHttpContext(
            new ReportsController(preActionReportService, Substitute.For<IPostActionReportService>()), userId: null);

        var result = await controller.PreAction(new ReportGenerationRequest { Format = "pdf" }, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        await preActionReportService.DidNotReceive().GenerateAsync(
            Arg.Any<ReportGenerationRequest>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PostAction_NoUserIdClaim_Returns401()
    {
        var postActionReportService = Substitute.For<IPostActionReportService>();
        var controller = AttachHttpContext(
            new ReportsController(Substitute.For<IPreActionReportService>(), postActionReportService), userId: null);

        var result = await controller.PostAction(new ReportGenerationRequest { Format = "pdf" }, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        await postActionReportService.DidNotReceive().GenerateAsync(
            Arg.Any<ReportGenerationRequest>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PreAction_GenerationFailed_Returns500ReportGenerationFailed()
    {
        var preActionReportService = Substitute.For<IPreActionReportService>();
        preActionReportService
            .GenerateAsync(Arg.Any<ReportGenerationRequest>(), 1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PreActionReportResult { Outcome = ReportGenerationOutcome.GenerationFailed });
        var controller = AttachHttpContext(
            new ReportsController(preActionReportService, Substitute.For<IPostActionReportService>()), userId: 1);

        var result = await controller.PreAction(new ReportGenerationRequest { Format = "pdf" }, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cachet.tracecounts.internal/errors/report-generation-failed");
    }

    [Fact]
    public async Task Test_PostAction_GenerationFailed_Returns500ReportGenerationFailed()
    {
        var postActionReportService = Substitute.For<IPostActionReportService>();
        postActionReportService
            .GenerateAsync(Arg.Any<ReportGenerationRequest>(), 1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PostActionReportResult { Outcome = ReportGenerationOutcome.GenerationFailed });
        var controller = AttachHttpContext(
            new ReportsController(Substitute.For<IPreActionReportService>(), postActionReportService), userId: 1);

        var result = await controller.PostAction(new ReportGenerationRequest { Format = "xlsx" }, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Type.Should().Be("https://cachet.tracecounts.internal/errors/report-generation-failed");
    }

    // ---- CAS-038 V-1: both preview routes require CachetAuthenticated; no session -> 401, never ----
    // rows. -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V1_PreActionPreview_Unauthenticated_Returns401()
    {
        var preActionReportService = Substitute.For<IPreActionReportService>();
        var controller = AttachHttpContext(
            new ReportsController(preActionReportService, Substitute.For<IPostActionReportService>()), userId: null);

        var result = await controller.PreActionPreview(activeTabLabel: null, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        await preActionReportService.DidNotReceive().PreviewAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V1_PostActionPreview_Unauthenticated_Returns401()
    {
        var postActionReportService = Substitute.For<IPostActionReportService>();
        var controller = AttachHttpContext(
            new ReportsController(Substitute.For<IPreActionReportService>(), postActionReportService), userId: null);

        var result = await controller.PostActionPreview(activeTabLabel: null, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        await postActionReportService.DidNotReceive().PreviewAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 V-6: activeTabLabel is accepted but never filters rows[] or appears in the -------
    // response body for either preview route — cosmetic only. ----------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Keyword Search")]
    [InlineData("some-other-tab-that-does-not-exist")]
    public async Task Test_V6_ActiveTabLabelIsCosmeticOnlyForBothPreviews(string? activeTabLabel)
    {
        // Arrange — neither IPreActionReportService.PreviewAsync nor
        // IPostActionReportService.PreviewAsync takes an activeTabLabel parameter at all, and neither
        // response DTO exposes such a field — this is a compile-time guarantee reinforced here at
        // runtime: the same service call happens and the same response object is returned regardless
        // of the query value.
        var preview = new PreActionReportPreview
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            GeneratedBy = "m.alvarez",
            Rows = new List<PreActionReportRow>()
        };
        var preActionReportService = Substitute.For<IPreActionReportService>();
        preActionReportService.PreviewAsync(1, "m.alvarez", Arg.Any<CancellationToken>()).Returns(preview);

        var postPreviewResult = new PostActionReportPreviewResult
        {
            Outcome = ReportPreviewOutcome.Generated,
            Response = new PostActionReportPreview
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                GeneratedBy = "m.alvarez",
                Rows = new List<PostActionReportRow>()
            }
        };
        var postActionReportService = Substitute.For<IPostActionReportService>();
        postActionReportService.PreviewAsync(1, "m.alvarez", Arg.Any<CancellationToken>()).Returns(postPreviewResult);

        var controller = AttachHttpContext(new ReportsController(preActionReportService, postActionReportService), userId: 1);

        // Act
        var preResult = await controller.PreActionPreview(activeTabLabel, CancellationToken.None);
        var postResult = await controller.PostActionPreview(activeTabLabel, CancellationToken.None);

        // Assert — same call, same response, regardless of activeTabLabel's value.
        var preOk = preResult.Should().BeOfType<OkObjectResult>().Subject;
        preOk.Value.Should().BeSameAs(preview);
        var postOk = postResult.Should().BeOfType<OkObjectResult>().Subject;
        postOk.Value.Should().BeSameAs(postPreviewResult.Response);
        await preActionReportService.Received(1).PreviewAsync(1, "m.alvarez", Arg.Any<CancellationToken>());
        await postActionReportService.Received(1).PreviewAsync(1, "m.alvarez", Arg.Any<CancellationToken>());
        typeof(PreActionReportPreview).GetProperties().Select(p => p.Name).Should().NotContain("ActiveTabLabel");
        typeof(PostActionReportPreview).GetProperties().Select(p => p.Name).Should().NotContain("ActiveTabLabel");
    }

    // ---- CAS-038 SC-4: neither preview endpoint writes an AuditLogEntry row (§12 decision: -------
    // preview is not an audited action) — unlike the audited POST download endpoints (SC-5 above). ---

    [Fact]
    public async Task Test_SC4_PreviewEndpointsWriteNoAuditRow()
    {
        // Arrange — real service instances (not pre-canned mock outcomes), so this proves the actual
        // §12 behavior: PreviewAsync never calls IAuditLogService.WriteAsync for either report kind.
        var searchResultRepository = Substitute.For<ISearchResultRepository>();
        var quarantineEntryRepository = Substitute.For<IQuarantineEntryRepository>();
        var preAuditLogService = Substitute.For<IAuditLogService>();
        searchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        quarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        var preService = BuildRealPreActionService(
            searchResultRepository, quarantineEntryRepository, Substitute.For<IReportDocumentWriter>(), preAuditLogService);

        var auditLogRepository = Substitute.For<IAuditLogRepository>();
        var postAuditLogService = Substitute.For<IAuditLogService>();
        auditLogRepository.ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>()).Returns((new List<AuditLogRow>(), 0));
        var postService = BuildRealPostActionService(auditLogRepository, Substitute.For<IReportDocumentWriter>(), postAuditLogService);

        var controller = AttachHttpContext(new ReportsController(preService, postService), userId: 1);

        // Act
        var preResult = await controller.PreActionPreview(activeTabLabel: null, CancellationToken.None);
        var postResult = await controller.PostActionPreview(activeTabLabel: null, CancellationToken.None);

        // Assert
        preResult.Should().BeOfType<OkObjectResult>();
        postResult.Should().BeOfType<OkObjectResult>();
        await preAuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        await postAuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-4 (compile-time half): GetPdf/GetXlsx no longer exist as members of this class ---------

    [Fact]
    public void Test_SC4_RemovedGetRoutesReturnNotFound()
    {
        // The runtime HTTP-level half of SC-4 ("GET /reports/{reportId}/pdf|xlsx return the
        // framework's default 404/route-not-found") is proven in
        // Cachet.ArchiveSync.Api.IntegrationTests.ReportsControllerTests, since a removed route has
        // no controller action left to invoke directly. This compile-time half asserts the actions
        // are gone from the type itself, reinforcing that absence at runtime via reflection.
        var actionNames = typeof(ReportsController).GetMethods().Select(m => m.Name).ToList();
        actionNames.Should().NotContain("GetPdf", "CAS-015 §5/§8 removed the GET /reports/{reportId}/pdf action");
        actionNames.Should().NotContain("GetXlsx", "CAS-015 §5/§8 removed the GET /reports/{reportId}/xlsx action");
    }
}
