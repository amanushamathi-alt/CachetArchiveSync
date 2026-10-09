using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Components.Shared;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Cachet.ArchiveSync.Web.Tests.Reports;

/// <summary>
/// CAS-032 §13.1 — proves SC-2..SC-12 and V1..V6 for <c>ReportModal.razor</c> via bUnit, with the
/// "CachetApi" named <see cref="HttpClient"/> redirected to <see cref="FakeReportsApiHandler"/> so no
/// real Api process is ever reached, and bUnit's JS interop in strict mode with
/// <c>cachetDownload.fromStream</c> as the only planned invocation. SC-1 (the trigger buttons on both
/// host pages) is proven in <c>KeywordSearchPageTests</c> / <c>ODriveSyncPageTests</c> per §13.2.
/// </summary>
public sealed class ReportModalTests : TestContext
{
    private const string KeywordSearchTab = "Keyword Search";
    private const string PdfContentType = "application/pdf";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string DownloadIdentifier = "cachetDownload.fromStream";
    private const string LoadingCopy = "Generating report… This may take a moment for large reports.";
    private const string PreviewErrorCopy = "Something went wrong generating this report. Please try again.";
    private const string GenerationFailedCopy = "Report generation failed. Please try again.";
    private const string SessionExpiredCopy = "Your session has expired — sign in again to see results.";
    private const string PreEmptyCopy = "No files are currently flagged, needing review, or informational across search and quarantine.";
    private const string PostEmptyCopy = "No actions have been recorded yet.";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly FakeReportsApiHandler _apiHandler = new();
    private readonly CapturingLoggerProvider _loggerProvider = new();
    private readonly TestAuthorizationContext _authContext;
    private readonly HttpClient _httpClient;

    public ReportModalTests()
    {
        _httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(_httpClient));
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));

        _authContext = this.AddTestAuthorization();
        _authContext.SetAuthorized("full-access-user");
        _authContext.SetClaims(new Claim("b2_capability", "Full Access"));

        // bUnit strict mode (the TestContext default): any JS call other than this planned one fails.
        JSInterop.Mode = JSRuntimeMode.Strict;
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static readonly DateTimeOffset GeneratedAtUtc = new(2026, 9, 25, 14, 3, 11, TimeSpan.Zero);

    private static PreActionReportRow PreRow(string name, string panel = "Filename Matches", string status = "Flagged") => new()
    {
        FileName = name,
        FilePath = $"/archive/finance/2024/{name}",
        Panel = panel,
        Status = status,
    };

    private static PostActionReportRow PostRow(string? name, string action = "Quarantined", string result = "Success") => new()
    {
        FileName = name,
        FilePath = name is null ? null : $"/archive/finance/2024/{name}",
        ActionTaken = action,
        SourceTab = KeywordSearchTab,
        Timestamp = new DateTimeOffset(2026, 9, 24, 9, 12, 40, TimeSpan.Zero),
        Result = result,
    };

    private static HttpResponseMessage PreviewResponse<TRow>(IReadOnlyList<TRow> rows, DateTimeOffset? generatedAt = null, string generatedBy = "malvarez") =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { generatedAt = generatedAt ?? GeneratedAtUtc, generatedBy, rows }),
        };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(Func<HttpResponseMessage> factory) =>
        (_, _) => Task.FromResult(factory());

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hold(TaskCompletionSource<HttpResponseMessage> tcs) =>
        (_, ct) => tcs.Task.WaitAsync(ct);

    private static TaskCompletionSource<HttpResponseMessage> NewHold() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage FileResponse(string? contentType, string? fileName = null, string? fileNameStar = null, int length = 16)
    {
        var content = new ByteArrayContent(Enumerable.Repeat((byte)0x25, length).ToArray());
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        if (fileName is not null || fileNameStar is not null)
        {
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = fileName,
                FileNameStar = fileNameStar,
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private void PreviewPre(params PreActionReportRow[] rows) =>
        _apiHandler.OnGetPreActionPreview = Respond(() => PreviewResponse(rows));

    private void PreviewPost(params PostActionReportRow[] rows) =>
        _apiHandler.OnGetPostActionPreview = Respond(() => PreviewResponse(rows));

    private JSRuntimeInvocationHandler PlanDownload()
    {
        var handler = JSInterop.SetupVoid(DownloadIdentifier, _ => true);
        handler.SetVoidResult();
        return handler;
    }

    private static IReadOnlyList<JSRuntimeInvocation> Calls(JSRuntimeInvocationHandler handler) =>
        handler.Invocations.Where(i => i.Identifier == DownloadIdentifier).ToList();

    private IRenderedComponent<ReportModal> RenderModal(string activeTabLabel = KeywordSearchTab) =>
        RenderComponent<ReportModal>(parameters => parameters.Add(p => p.ActiveTabLabel, activeTabLabel));

    private static Task OpenAsync(IRenderedComponent<ReportModal> cut, ReportKind kind) =>
        cut.InvokeAsync(() => cut.Instance.OpenAsync(kind));

    private static IElement PdfButton(IRenderedComponent<ReportModal> cut) => cut.Find(".cachet-report-download-pdf-btn");

    private static IElement XlsxButton(IRenderedComponent<ReportModal> cut) => cut.Find(".cachet-report-download-xlsx-btn");

    private static IElement CloseButton(IRenderedComponent<ReportModal> cut) => cut.Find(".cachet-report-close-btn");

    private static bool IsDisabled(IElement button) => button.HasAttribute("disabled");

    private static string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static IReadOnlyList<string> SummaryItems(IRenderedComponent<ReportModal> cut) =>
        cut.FindAll(".cachet-report-summary-item").Select(e => Normalize(e.TextContent)).ToList();

    private static IReadOnlyList<string> Banners(IRenderedComponent<ReportModal> cut) =>
        cut.FindAll(".cachet-report-error-banner").Select(e => Normalize(e.TextContent)).ToList();

    private List<ReportGenerationRequest> PostedBodies() =>
        _apiHandler.Requests
            .Select((r, i) => (Request: r, Body: _apiHandler.RequestBodies[i]))
            .Where(x => x.Request.Method == HttpMethod.Post)
            .Select(x => JsonSerializer.Deserialize<ReportGenerationRequest>(x.Body!, WebJson)!)
            .ToList();

    private string LogText => string.Join(Environment.NewLine, _loggerProvider.Messages);

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC2_InProgressStateShownBeforePreviewCompletes()
    {
        var hold = NewHold();
        _apiHandler.OnGetPreActionPreview = Hold(hold);
        var cut = RenderModal();

        var openTask = OpenAsync(cut, ReportKind.PreAction);

        // Before the preview response exists, the modal is already rendered in its loading state.
        openTask.IsCompleted.Should().BeFalse("the preview GET is still being held");
        cut.Find(".cachet-modal").GetAttribute("role").Should().Be("dialog");
        cut.Find("#cachet-report-title").TextContent.Should().Be("PRE-ACTION IMPACT REPORT");
        cut.Find(".cachet-modal-body").GetAttribute("aria-busy").Should().Be("true");
        var status = cut.Find(".cachet-modal-body [aria-live]");
        status.GetAttribute("aria-live").Should().Be("polite");
        status.TextContent.Should().Be(LoadingCopy);
        IsDisabled(PdfButton(cut)).Should().BeTrue();
        IsDisabled(XlsxButton(cut)).Should().BeTrue();
        IsDisabled(CloseButton(cut)).Should().BeFalse("Close stays enabled while loading (§8.3)");
        _apiHandler.PreviewCallCount.Should().Be(1);

        hold.SetResult(PreviewResponse(new[] { PreRow("Q3-ledger.xlsx") }));
        await openTask;

        cut.WaitForAssertion(() => cut.Find(".cachet-modal-body").GetAttribute("aria-busy").Should().Be("false"));
        cut.Markup.Should().NotContain(LoadingCopy);
    }

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_PreActionPreviewRendersHeaderSummaryAndColumns()
    {
        var rows = new[]
        {
            PreRow("Q3-ledger.xlsx", "Filename Matches", "Flagged"),
            PreRow("memo.docx", "Content Matches", "Needs Review"),
            PreRow("payroll.pdf", "Quarantine", "Flagged"),
            PreRow("notes.txt", "Content Matches", "Informational"),
        };
        // A non-UTC offset proves the meta line converts to UTC (19:33:11 +05:30 == 14:03:11 UTC).
        _apiHandler.OnGetPreActionPreview = Respond(() =>
            PreviewResponse(rows, new DateTimeOffset(2026, 9, 25, 19, 33, 11, TimeSpan.FromHours(5.5)), "malvarez"));
        var cut = RenderModal();

        await OpenAsync(cut, ReportKind.PreAction);

        cut.Find("#cachet-report-title").TextContent.Should().Be("PRE-ACTION IMPACT REPORT");
        cut.Find(".cachet-report-subtitle").TextContent.Should().Be(KeywordSearchTab);
        cut.Find(".cachet-report-close").GetAttribute("aria-label").Should().Be("Close report");
        Normalize(cut.Find(".cachet-report-meta").TextContent).Should().Be("Generated 2026-09-25 14:03:11 UTC by malvarez.");

        SummaryItems(cut).Should().Equal(
            "Total: 4",
            "Filename Matches: 1",
            "Content Matches: 2",
            "Quarantine: 1",
            "Flagged: 2",
            "Needs Review: 1",
            "Informational: 1");

        cut.FindAll(".cachet-report-table thead th").Select(th => th.TextContent).Should().Equal("File", "Panel", "Status");
        var bodyRows = cut.FindAll(".cachet-report-table tbody tr").Where(tr => tr.QuerySelector("td") is not null).ToList();
        bodyRows.Should().HaveCount(rows.Length, "every preview row is rendered");
        foreach (var row in rows)
        {
            var tr = bodyRows.Single(r => r.QuerySelector(".cachet-cell-name")!.TextContent == row.FileName);
            var cells = tr.QuerySelectorAll("td");
            cells[0].QuerySelector(".cachet-cell-name")!.GetAttribute("title").Should().Be(row.FileName);
            cells[0].QuerySelector(".cachet-cell-path")!.TextContent.Should().Be(row.FilePath);
            cells[0].QuerySelector(".cachet-cell-path")!.GetAttribute("title").Should().Be(row.FilePath);
            cells[1].TextContent.Should().Be(row.Panel);
            var chip = cells[2].QuerySelector("span")!;
            chip.TextContent.Should().Be(row.Status);
            chip.ClassList.Should().Contain("cachet-chip");
        }

        bodyRows.Single(r => r.TextContent.Contains("Q3-ledger.xlsx")).QuerySelector(".cachet-chip")!.ClassList.Should().Contain("cachet-chip-flagged");
        bodyRows.Single(r => r.TextContent.Contains("memo.docx")).QuerySelector(".cachet-chip")!.ClassList.Should().Contain("cachet-chip-needs-review");
        bodyRows.Single(r => r.TextContent.Contains("notes.txt")).QuerySelector(".cachet-chip")!.ClassList.Should().Contain("cachet-chip-informational");
        cut.Find(".cachet-report-disclosure").TextContent.Should().Contain("not saved on the server");
    }

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC4_PostActionPreviewRendersColumnsFailuresAndNullFiles()
    {
        PreviewPost(
            PostRow("Q3-ledger.xlsx", "Quarantined", "Success"),
            PostRow("payroll.pdf", "Permanently Deleted", "Failure"),
            PostRow(null, "Keyword Search Submitted", "Success"));
        var cut = RenderModal();

        await OpenAsync(cut, ReportKind.PostAction);

        cut.Find("#cachet-report-title").TextContent.Should().Be("POST-ACTION OUTCOME REPORT");
        cut.FindAll(".cachet-report-table thead th").Select(th => th.TextContent)
            .Should().Equal("File", "Action Taken", "Source Tab", "Timestamp", "Result");

        var bodyRows = cut.FindAll(".cachet-report-table tbody tr").Where(tr => tr.QuerySelector("td") is not null).ToList();
        bodyRows.Should().HaveCount(3);

        var success = bodyRows.Single(r => r.TextContent.Contains("Q3-ledger.xlsx")).QuerySelectorAll("td");
        success[0].QuerySelector(".cachet-cell-path")!.TextContent.Should().Be("/archive/finance/2024/Q3-ledger.xlsx");
        success[1].TextContent.Should().Be("Quarantined");
        success[2].TextContent.Should().Be(KeywordSearchTab);
        success[3].TextContent.Should().Be("2026-09-24 09:12:40 UTC");
        success[4].TextContent.Trim().Should().Be("Success");
        success[4].QuerySelector(".cachet-chip").Should().BeNull("only Failure rows get the visual chip");

        var failure = bodyRows.Single(r => r.TextContent.Contains("payroll.pdf")).QuerySelectorAll("td");
        failure[1].TextContent.Should().Be("Permanently Deleted");
        var chip = failure[4].QuerySelector(".cachet-chip")!;
        chip.TextContent.Should().Be("Failure", "failures are always shown, never hidden (CAS-015 V5)");
        chip.ClassList.Should().Contain("cachet-chip-flagged");

        var taskLevel = bodyRows.Single(r => r.TextContent.Contains("Keyword Search Submitted")).QuerySelectorAll("td");
        taskLevel[0].QuerySelector(".cachet-cell-name")!.TextContent.Should().Be("—");
        taskLevel[0].QuerySelector(".cachet-cell-path").Should().BeNull();

        SummaryItems(cut).Should().Equal(
            "Total: 3",
            "Quarantined: 1",
            "Permanently Deleted: 1",
            "Keyword Search Submitted: 1",
            "Success: 2",
            "Failure: 1");
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_DownloadPostsFormatAndDeliversStreamWithServerFilename()
    {
        var download = PlanDownload();
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        PreviewPost(PostRow("Q3-ledger.xlsx"));
        _apiHandler.OnPostPreAction = (request, _) =>
        {
            var format = JsonSerializer.Deserialize<ReportGenerationRequest>(_apiHandler.RequestBodies[^1]!, WebJson)!.Format;
            return Task.FromResult(format == "pdf"
                ? FileResponse(PdfContentType, fileName: "\"pre-action-report-20260925140311.pdf\"")
                : FileResponse(XlsxContentType, fileNameStar: "pre-action-report-20260925140312.xlsx"));
        };
        // No Content-Disposition at all → the §8.4 step 4 fixed fallback name.
        _apiHandler.OnPostPostAction = Respond(() => FileResponse(PdfContentType));
        var cut = RenderModal();

        // Pre-Action, PDF.
        await OpenAsync(cut, ReportKind.PreAction);
        await PdfButton(cut).ClickAsync(new MouseEventArgs());

        _apiHandler.PostCallCount.Should().Be(1, "exactly one POST per click");
        var firstPost = _apiHandler.Requests.Single(r => r.Method == HttpMethod.Post);
        firstPost.RequestUri!.AbsolutePath.Should().Be("/api/reports/pre-action");
        PostedBodies()[0].Should().BeEquivalentTo(new ReportGenerationRequest { ActiveTabLabel = KeywordSearchTab, Format = "pdf" });
        Calls(download).Should().HaveCount(1);
        Calls(download)[0].Arguments[0].Should().Be("pre-action-report-20260925140311.pdf", "the Content-Disposition filename, trimmed of quotes");
        Calls(download)[0].Arguments[1].Should().BeOfType<DotNetStreamReference>();

        // Pre-Action, XLSX (filename* form).
        await XlsxButton(cut).ClickAsync(new MouseEventArgs());

        _apiHandler.PostCallCount.Should().Be(2);
        PostedBodies()[1].Should().BeEquivalentTo(new ReportGenerationRequest { ActiveTabLabel = KeywordSearchTab, Format = "xlsx" });
        Calls(download).Should().HaveCount(2);
        Calls(download)[1].Arguments[0].Should().Be("pre-action-report-20260925140312.xlsx");

        // Post-Action, PDF: routes to reports/post-action; no filename header → fallback.
        await OpenAsync(cut, ReportKind.PostAction);
        await PdfButton(cut).ClickAsync(new MouseEventArgs());

        _apiHandler.PostCallCount.Should().Be(3);
        _apiHandler.Requests.Where(r => r.Method == HttpMethod.Post).Last().RequestUri!.AbsolutePath.Should().Be("/api/reports/post-action");
        PostedBodies()[2].Should().BeEquivalentTo(new ReportGenerationRequest { ActiveTabLabel = KeywordSearchTab, Format = "pdf" });
        Calls(download).Should().HaveCount(3);
        Calls(download)[2].Arguments[0].Should().Be("post-action-report.pdf");
        Banners(cut).Should().BeEmpty("no success banner is shown — the browser's own download UI is the confirmation");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf", "Generating PDF…", "Download PDF")]
    [InlineData("xlsx", "Generating XLSX…", "Download XLSX")]
    public async Task Test_SC6_DownloadInProgressStateAndReenable(string format, string inFlightLabel, string idleLabel)
    {
        var download = PlanDownload();
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        var hold = NewHold();
        _apiHandler.OnPostPreAction = Hold(hold);
        var cut = RenderModal();
        await OpenAsync(cut, ReportKind.PreAction);

        IElement Clicked() => format == "pdf" ? PdfButton(cut) : XlsxButton(cut);
        IElement Other() => format == "pdf" ? XlsxButton(cut) : PdfButton(cut);

        var clickTask = Clicked().ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Normalize(Clicked().TextContent).Should().Be(inFlightLabel));
        Clicked().QuerySelector(".cachet-report-spinner").Should().NotBeNull();
        IsDisabled(Clicked()).Should().BeTrue();
        IsDisabled(Other()).Should().BeTrue("both Download buttons are disabled while one runs (V3)");
        IsDisabled(CloseButton(cut)).Should().BeFalse("Close stays enabled during a download (§8.4 step 2)");

        hold.SetResult(FileResponse(format == "pdf" ? PdfContentType : XlsxContentType, fileName: $"pre-action-report-20260925140311.{format}"));
        await clickTask;

        cut.WaitForAssertion(() => Normalize(Clicked().TextContent).Should().Be(idleLabel));
        IsDisabled(PdfButton(cut)).Should().BeFalse();
        IsDisabled(XlsxButton(cut)).Should().BeFalse();
        Calls(download).Should().HaveCount(1);
        cut.FindAll(".cachet-report-table tbody tr td").Should().NotBeEmpty("the table is not refreshed or cleared by a download");
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReportKind.PreAction, PreEmptyCopy)]
    [InlineData(ReportKind.PostAction, PostEmptyCopy)]
    public async Task Test_SC7_EmptyReportShowsCopyAndAllowsDownload(ReportKind kind, string expectedCopy)
    {
        var download = PlanDownload();
        PreviewPre();
        PreviewPost();
        _apiHandler.OnPostPreAction = Respond(() => FileResponse(PdfContentType, fileName: "pre-action-report-20260925140311.pdf"));
        _apiHandler.OnPostPostAction = Respond(() => FileResponse(PdfContentType, fileName: "post-action-report-20260925140311.pdf"));
        var cut = RenderModal();

        await OpenAsync(cut, kind);

        cut.Find(".cachet-modal-body .cachet-modal-empty").TextContent.Should().Be(expectedCopy);
        cut.FindAll(".cachet-report-table").Should().BeEmpty();
        SummaryItems(cut)[0].Should().Be("Total: 0");
        IsDisabled(PdfButton(cut)).Should().BeFalse("an empty report is still a valid record (§8.5)");
        IsDisabled(XlsxButton(cut)).Should().BeFalse();

        await PdfButton(cut).ClickAsync(new MouseEventArgs());

        _apiHandler.PostCallCount.Should().Be(1);
        Calls(download).Should().HaveCount(1);
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC8_PreviewRetryAndDownloadFailureStates()
    {
        _apiHandler.OnGetPreActionPreview = Respond(() => Status(HttpStatusCode.InternalServerError));
        _apiHandler.OnPostPreAction = Respond(() => Status(HttpStatusCode.InternalServerError));
        var cut = RenderModal();

        await OpenAsync(cut, ReportKind.PreAction);

        Banners(cut).Should().ContainSingle().Which.Should().Contain(PreviewErrorCopy);
        var retry = cut.Find(".cachet-report-retry-btn");
        retry.TextContent.Should().Be("Retry");
        IsDisabled(PdfButton(cut)).Should().BeTrue();
        _apiHandler.PreviewCallCount.Should().Be(1);

        // Retry re-issues the preview GET; this time it succeeds.
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        await retry.ClickAsync(new MouseEventArgs());

        _apiHandler.PreviewCallCount.Should().Be(2, "Retry re-runs the §8.3 load");
        Banners(cut).Should().BeEmpty();
        cut.Markup.Should().Contain("Q3-ledger.xlsx");

        // A download 500 shows the generation-failed banner, keeps the table, re-enables buttons.
        await PdfButton(cut).ClickAsync(new MouseEventArgs());

        Banners(cut).Should().ContainSingle().Which.Should().Be(GenerationFailedCopy);
        cut.Find(".cachet-report-table").TextContent.Should().Contain("Q3-ledger.xlsx", "the table stays visible");
        IsDisabled(PdfButton(cut)).Should().BeFalse();
        IsDisabled(XlsxButton(cut)).Should().BeFalse();
        JSInterop.Invocations.Where(i => i.Identifier == DownloadIdentifier).Should().BeEmpty("a failed generation is never handed to the browser");
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC9_StalePreviewResponseIgnored()
    {
        // (a) A newer OpenAsync supersedes a still-pending one.
        var stalePre = NewHold();
        _apiHandler.OnGetPreActionPreview = Hold(stalePre);
        PreviewPost(PostRow("current-post-row.pdf"));
        var cut = RenderModal();

        var staleOpen = OpenAsync(cut, ReportKind.PreAction);
        await OpenAsync(cut, ReportKind.PostAction);
        cut.Markup.Should().Contain("current-post-row.pdf");

        stalePre.SetResult(PreviewResponse(new[] { PreRow("stale-pre-row.xlsx") }, generatedBy: "stale-user"));
        await staleOpen;

        cut.Find("#cachet-report-title").TextContent.Should().Be("POST-ACTION OUTCOME REPORT");
        cut.Markup.Should().Contain("current-post-row.pdf");
        cut.Markup.Should().NotContain("stale-pre-row.xlsx", "a superseded open's response must never overwrite state");
        cut.Markup.Should().NotContain("stale-user");

        // (b) A response arriving after Close is ignored — the modal stays closed and empty.
        var afterClose = NewHold();
        _apiHandler.OnGetPreActionPreview = Hold(afterClose);
        var closedOpen = OpenAsync(cut, ReportKind.PreAction);
        cut.Markup.Should().Contain(LoadingCopy);
        await CloseButton(cut).ClickAsync(new MouseEventArgs());
        cut.Markup.Should().BeEmpty();

        afterClose.SetResult(PreviewResponse(new[] { PreRow("after-close-row.xlsx") }));
        await closedOpen;

        cut.Markup.Should().BeEmpty("a response for a closed modal must not reopen or populate it");

        // (c) A stale error after a newer open is likewise ignored.
        var staleError = NewHold();
        _apiHandler.OnGetPreActionPreview = Hold(staleError);
        var staleErrorOpen = OpenAsync(cut, ReportKind.PreAction);
        await OpenAsync(cut, ReportKind.PostAction);
        staleError.SetResult(Status(HttpStatusCode.InternalServerError));
        await staleErrorOpen;

        Banners(cut).Should().BeEmpty();
        cut.Markup.Should().Contain("current-post-row.pdf");
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC10_NoServerSidePersistenceOrCachingOfReports()
    {
        var download = PlanDownload();
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        _apiHandler.OnPostPreAction = Respond(() => FileResponse(PdfContentType, fileName: "pre-action-report-20260925140311.pdf"));
        var cut = RenderModal();

        // Each open is a fresh GET; nothing survives a Close.
        await OpenAsync(cut, ReportKind.PreAction);
        await CloseButton(cut).ClickAsync(new MouseEventArgs());
        cut.Markup.Should().BeEmpty();
        await OpenAsync(cut, ReportKind.PreAction);
        _apiHandler.PreviewCallCount.Should().Be(2, "a second open must issue a second preview GET (no cache)");

        // Each download is a fresh POST.
        await PdfButton(cut).ClickAsync(new MouseEventArgs());
        await PdfButton(cut).ClickAsync(new MouseEventArgs());
        _apiHandler.PostCallCount.Should().Be(2, "a second download must issue a second POST (no cache)");
        Calls(download).Should().HaveCount(2);

        // Static checks: the component has no cache dependency and no file-system write.
        var componentType = typeof(ReportModal);
        var memberTypes = componentType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(p => p.PropertyType)
            .Concat(componentType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).Select(f => f.FieldType))
            .Select(t => t.FullName ?? t.Name)
            .ToList();
        memberTypes.Should().NotContain(t => t.Contains("IMemoryCache") || t.Contains("IDistributedCache") || t.Contains("FileStream"));

        var source = File.ReadAllText(Path.Combine(FindWebProjectRoot(), "Components", "Shared", "ReportModal.razor"));
        foreach (var forbidden in new[]
                 {
                     "File.Write", "File.Create", "File.Open", "FileStream", "Path.GetTempFileName", "Path.GetTempPath",
                     "IMemoryCache", "IDistributedCache", "MemoryCache",
                 })
        {
            source.Should().NotContain(forbidden, $"SC-10: ReportModal must not persist or cache report data ({forbidden})");
        }
    }

    // -----------------------------------------------------------------------------------------
    // SC-11
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC11_NoSensitiveDataInReportModalLogs()
    {
        const string PathMarker = "ZZ-SENSITIVE-PATH-MARKER";
        const string FileNameMarker = "ZZ-DISPOSITION-MARKER";
        const string CookieMarker = "ZZ-SESSION-COOKIE-VALUE";
        const string ProblemMarker = "ZZ-PROBLEM-DETAILS-MARKER";
        const string B2KeyMarker = "K005ZZ-B2-APPLICATION-KEY";

        _httpClient.DefaultRequestHeaders.Add("Cookie", $".AspNetCore.Cookies={CookieMarker}; b2key={B2KeyMarker}");
        var download = PlanDownload();
        PreviewPre(new PreActionReportRow
        {
            FileName = $"{PathMarker}.xlsx",
            FilePath = $"/archive/{PathMarker}/{PathMarker}.xlsx",
            Panel = "Filename Matches",
            Status = "Flagged",
        });
        PreviewPost(new PostActionReportRow
        {
            FileName = $"{PathMarker}.pdf",
            FilePath = $"/archive/{PathMarker}/{PathMarker}.pdf",
            ActionTaken = "Quarantined",
            SourceTab = KeywordSearchTab,
            Timestamp = GeneratedAtUtc,
            Result = "Success",
        });
        _apiHandler.OnPostPreAction = Respond(() => FileResponse(PdfContentType, fileName: $"{FileNameMarker}.pdf", length: 42));
        _apiHandler.OnPostPostAction = Respond(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($"{{\"type\":\"report-generation-failed\",\"detail\":\"{ProblemMarker}\"}}", Encoding.UTF8, "application/problem+json"),
        });
        var cut = RenderModal();

        await OpenAsync(cut, ReportKind.PreAction);
        await PdfButton(cut).ClickAsync(new MouseEventArgs());
        await OpenAsync(cut, ReportKind.PostAction);
        await XlsxButton(cut).ClickAsync(new MouseEventArgs());

        Calls(download).Should().HaveCount(1);
        var logs = LogText;
        logs.Should().NotContain(PathMarker, "row contents (file names/paths) must never be logged (§11)");
        logs.Should().NotContain(FileNameMarker, "the Content-Disposition header must never be logged (§11)");
        logs.Should().NotContain(CookieMarker);
        logs.Should().NotContain(B2KeyMarker);
        logs.Should().NotContain(ProblemMarker, "ProblemDetails bodies must never be logged (§11)");

        // Only the §11 fields are logged.
        logs.Should().Contain("ReportModal: opened PreAction from Keyword Search");
        logs.Should().Contain("ReportModal: preview loaded PreAction, RowCount=1");
        logs.Should().Contain("ReportModal: download delivered PreAction pdf, Bytes=42");
        logs.Should().Contain("ReportModal: unexpected 500 from POST /reports/post-action");
    }

    // -----------------------------------------------------------------------------------------
    // SC-12
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC12_ReadOnlySessionCanViewAndDownloadReports()
    {
        _authContext.SetAuthorized("read-only-user");
        _authContext.SetClaims(new Claim("b2_capability", "Read Only"));
        var download = PlanDownload();
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        PreviewPost(PostRow("Q3-ledger.xlsx"));
        _apiHandler.OnPostPreAction = (_, _) => Task.FromResult(
            PostedBodies()[^1].Format == "pdf" ? FileResponse(PdfContentType, "pre.pdf") : FileResponse(XlsxContentType, "pre.xlsx"));
        _apiHandler.OnPostPostAction = (_, _) => Task.FromResult(
            PostedBodies()[^1].Format == "pdf" ? FileResponse(PdfContentType, "post.pdf") : FileResponse(XlsxContentType, "post.xlsx"));
        var cut = RenderModal();

        foreach (var kind in new[] { ReportKind.PreAction, ReportKind.PostAction })
        {
            await OpenAsync(cut, kind);

            cut.Markup.Should().Contain("Q3-ledger.xlsx", $"a Read Only session can view the {kind} report");
            IsDisabled(PdfButton(cut)).Should().BeFalse("no Full Access gating (§8.1)");
            IsDisabled(XlsxButton(cut)).Should().BeFalse();

            await PdfButton(cut).ClickAsync(new MouseEventArgs());
            await XlsxButton(cut).ClickAsync(new MouseEventArgs());
        }

        Banners(cut).Should().BeEmpty();
        Calls(download).Select(i => i.Arguments[0]).Should().Equal("pre.pdf", "pre.xlsx", "post.pdf", "post.xlsx");
    }

    // -----------------------------------------------------------------------------------------
    // V-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V1_DownloadButtonsDisabledUntilPreviewSucceeds()
    {
        // Before the modal is opened nothing is rendered at all.
        var cut = RenderModal();
        cut.Markup.Should().BeEmpty();

        // While loading: disabled.
        var hold = NewHold();
        _apiHandler.OnGetPreActionPreview = Hold(hold);
        var openTask = OpenAsync(cut, ReportKind.PreAction);
        IsDisabled(PdfButton(cut)).Should().BeTrue("disabled while the preview is loading");
        IsDisabled(XlsxButton(cut)).Should().BeTrue();

        // After an error: still disabled.
        hold.SetResult(Status(HttpStatusCode.InternalServerError));
        await openTask;
        IsDisabled(PdfButton(cut)).Should().BeTrue("disabled after a preview error");
        IsDisabled(XlsxButton(cut)).Should().BeTrue();

        // After a zero-row success: enabled.
        PreviewPre();
        await OpenAsync(cut, ReportKind.PreAction);
        IsDisabled(PdfButton(cut)).Should().BeFalse("a zero-row success still enables downloads");
        IsDisabled(XlsxButton(cut)).Should().BeFalse();

        _apiHandler.PostCallCount.Should().Be(0, "no POST was ever sent while the buttons were disabled");
    }

    // -----------------------------------------------------------------------------------------
    // V-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V2_DownloadSendsExactFormatLiteral()
    {
        PlanDownload();
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        _apiHandler.OnPostPreAction = (_, _) => Task.FromResult(
            PostedBodies()[^1].Format == "pdf" ? FileResponse(PdfContentType, "a.pdf") : FileResponse(XlsxContentType, "a.xlsx"));
        var cut = RenderModal();
        await OpenAsync(cut, ReportKind.PreAction);

        await PdfButton(cut).ClickAsync(new MouseEventArgs());
        await XlsxButton(cut).ClickAsync(new MouseEventArgs());

        var rawBodies = _apiHandler.Requests.Select((r, i) => (r, i)).Where(x => x.r.Method == HttpMethod.Post)
            .Select(x => _apiHandler.RequestBodies[x.i]!).ToList();
        rawBodies.Should().HaveCount(2);
        rawBodies[0].Should().Contain("\"format\":\"pdf\"");
        rawBodies[1].Should().Contain("\"format\":\"xlsx\"");
        PostedBodies().Select(b => b.Format).Should().Equal("pdf", "xlsx");
        PostedBodies().Should().OnlyContain(b => b.Format == "pdf" || b.Format == "xlsx", "no other format value can be sent");
    }

    // -----------------------------------------------------------------------------------------
    // V-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V3_SecondDownloadClickIgnoredWhileInFlight()
    {
        var download = PlanDownload();
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        var hold = NewHold();
        _apiHandler.OnPostPreAction = Hold(hold);
        var cut = RenderModal();
        await OpenAsync(cut, ReportKind.PreAction);

        var firstClick = PdfButton(cut).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => IsDisabled(XlsxButton(cut)).Should().BeTrue());

        // Additional clicks on either button while the first is in flight are ignored. The disabled
        // attribute is the UI gate; the component's own DownloadsEnabled guard is the code-level gate,
        // proved by dispatching the handler directly (a disabled button may not dispatch in bUnit).
        var downloadMethod = typeof(ReportModal).GetMethod("DownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => (Task)downloadMethod.Invoke(cut.Instance, new object[] { "xlsx" })!);
        await cut.InvokeAsync(() => (Task)downloadMethod.Invoke(cut.Instance, new object[] { "pdf" })!);

        _apiHandler.PostCallCount.Should().Be(1, "at most one download is in flight per modal");

        hold.SetResult(FileResponse(PdfContentType, "pre.pdf"));
        await firstClick;

        _apiHandler.PostCallCount.Should().Be(1);
        Calls(download).Should().HaveCount(1, "no duplicate generation or duplicate delivery");
    }

    // -----------------------------------------------------------------------------------------
    // V-4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("preview-500")]
    [InlineData("preview-404")]
    [InlineData("preview-transport")]
    [InlineData("preview-malformed-json")]
    [InlineData("preview-null-body-pre")]
    [InlineData("preview-null-body-post")]
    [InlineData("download-400")]
    [InlineData("download-503")]
    [InlineData("download-transport")]
    [InlineData("download-js-exception")]
    public async Task Test_V4_ServerErrorsRenderInlineBannerNotCrash(string scenario)
    {
        var download = JSInterop.SetupVoid(DownloadIdentifier, _ => true);
        if (scenario == "download-js-exception")
        {
            download.SetException(new JSException("browser refused the download"));
        }
        else
        {
            download.SetVoidResult();
        }

        var kind = scenario == "preview-null-body-post" ? ReportKind.PostAction : ReportKind.PreAction;
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> previewHandler = scenario switch
        {
            "preview-500" => Respond(() => Status(HttpStatusCode.InternalServerError)),
            "preview-404" => Respond(() => Status(HttpStatusCode.NotFound)),
            "preview-transport" => (_, _) => throw new HttpRequestException("connection refused"),
            "preview-malformed-json" => Respond(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
            }),
            "preview-null-body-pre" or "preview-null-body-post" => Respond(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json"),
            }),
            _ => Respond(() => PreviewResponse(new[] { PreRow("Q3-ledger.xlsx") })),
        };
        _apiHandler.OnGetPreActionPreview = previewHandler;
        _apiHandler.OnGetPostActionPreview = previewHandler;
        _apiHandler.OnPostPreAction = scenario switch
        {
            "download-400" => Respond(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"type\":\"validation-failed\"}", Encoding.UTF8, "application/problem+json"),
            }),
            "download-503" => Respond(() => Status(HttpStatusCode.ServiceUnavailable)),
            "download-transport" => (_, _) => throw new HttpRequestException("connection reset"),
            _ => Respond(() => FileResponse(XlsxContentType, "pre.xlsx")),
        };
        var cut = RenderModal();

        var openAct = () => OpenAsync(cut, kind);
        await openAct.Should().NotThrowAsync();
        cut.Find(".cachet-modal").Should().NotBeNull("never a blank modal");

        if (scenario.StartsWith("preview-", StringComparison.Ordinal))
        {
            Banners(cut).Should().ContainSingle().Which.Should().Contain(PreviewErrorCopy);
            cut.Markup.Should().NotContain("not json", "raw response text is never shown");
            IsDisabled(PdfButton(cut)).Should().BeTrue();
            return;
        }

        var downloadAct = () => XlsxButton(cut).ClickAsync(new MouseEventArgs());
        await downloadAct.Should().NotThrowAsync();

        Banners(cut).Should().ContainSingle().Which.Should().Be("The XLSX download failed. Please try again.");
        cut.Markup.Should().NotContain("validation-failed", "raw ProblemDetails text is never shown");
        cut.Find(".cachet-report-table").TextContent.Should().Contain("Q3-ledger.xlsx", "the table stays visible");
        IsDisabled(XlsxButton(cut)).Should().BeFalse("the buttons re-enable after a failed download");
    }

    // -----------------------------------------------------------------------------------------
    // V-5
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf", "text/html", "The PDF download failed. Please try again.")]
    [InlineData("pdf", null, "The PDF download failed. Please try again.")]
    [InlineData("pdf", XlsxContentType, "The PDF download failed. Please try again.")]
    [InlineData("xlsx", PdfContentType, "The XLSX download failed. Please try again.")]
    [InlineData("xlsx", "application/octet-stream", "The XLSX download failed. Please try again.")]
    public async Task Test_V5_UnexpectedContentTypeNotDeliveredToBrowser(string format, string? contentType, string expectedBanner)
    {
        var download = PlanDownload();
        PreviewPre(PreRow("Q3-ledger.xlsx"));
        _apiHandler.OnPostPreAction = Respond(() => FileResponse(contentType, fileName: $"pre-action-report.{format}"));
        var cut = RenderModal();
        await OpenAsync(cut, ReportKind.PreAction);

        await (format == "pdf" ? PdfButton(cut) : XlsxButton(cut)).ClickAsync(new MouseEventArgs());

        _apiHandler.PostCallCount.Should().Be(1);
        Calls(download).Should().BeEmpty("a 200 with an unexpected Content-Type is never passed to the browser");
        Banners(cut).Should().ContainSingle().Which.Should().Be(expectedBanner);
    }

    // -----------------------------------------------------------------------------------------
    // V-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V6_UnauthorizedShowsSessionExpiredBanner()
    {
        // (a) 401 from the preview GET.
        _apiHandler.OnGetPreActionPreview = Respond(() => Status(HttpStatusCode.Unauthorized));
        var cut = RenderModal();

        await OpenAsync(cut, ReportKind.PreAction);

        var banner = cut.FindAll(".cachet-report-error-banner").Single();
        Normalize(banner.TextContent).Should().Be($"{SessionExpiredCopy} Sign in");
        var link = banner.QuerySelector("a")!;
        link.GetAttribute("href").Should().Be("login");
        link.TextContent.Should().Be("Sign in");
        cut.FindAll(".cachet-report-retry-btn").Should().BeEmpty("a 401 shows the session banner, not the Retry banner");
        IsDisabled(PdfButton(cut)).Should().BeTrue();
        IsDisabled(XlsxButton(cut)).Should().BeTrue();
        _apiHandler.PreviewCallCount.Should().Be(1, "no automatic retry after a 401");

        // (b) 401 from the download POST.
        PreviewPost(PostRow("Q3-ledger.xlsx"));
        _apiHandler.OnPostPostAction = Respond(() => Status(HttpStatusCode.Unauthorized));
        await OpenAsync(cut, ReportKind.PostAction);
        await PdfButton(cut).ClickAsync(new MouseEventArgs());

        var downloadBanner = cut.FindAll(".cachet-report-error-banner").Single();
        Normalize(downloadBanner.TextContent).Should().Be($"{SessionExpiredCopy} Sign in");
        downloadBanner.QuerySelector("a")!.GetAttribute("href").Should().Be("login");
        IsDisabled(PdfButton(cut)).Should().BeTrue("downloads are disabled after a 401");
        IsDisabled(XlsxButton(cut)).Should().BeTrue();
        _apiHandler.PostCallCount.Should().Be(1, "no automatic retry after a 401");
        JSInterop.Invocations.Where(i => i.Identifier == DownloadIdentifier).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Test infrastructure
    // -----------------------------------------------------------------------------------------

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Cachet.ArchiveSync.Web");
            if (File.Exists(Path.Combine(candidate, "Cachet.ArchiveSync.Web.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cachet.ArchiveSync.Web project folder not found above the test output directory.");
    }

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — this component only ever
    /// requests the "CachetApi" named client. Mirrors QuarantineQueueModalTests' private helper.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
