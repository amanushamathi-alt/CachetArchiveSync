using System.Net;
using System.Net.Http.Json;
using Bunit;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Components.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Cachet.ArchiveSync.Web.Tests.FileViewer;

/// <summary>
/// CAS-040 §13 — <c>FileViewerModal.razor</c> via bUnit, with the "CachetApi" named
/// <see cref="HttpClient"/> redirected to <see cref="FakeFileViewerApiHandler"/>: SC-14 (iframe per
/// view mode, sandbox rules, banners), SC-15 (DownloadOnly and metadata-error states) and SC-16
/// (Download is a plain navigation, no JS download/stream call). bUnit's JSInterop is Strict, so
/// any unplanned JS call fails the test; only the §8.4 focus call is expected.
/// </summary>
public sealed class FileViewerModalTests : TestContext
{
    private const int FileIndexId = 100245;

    private readonly FakeFileViewerApiHandler _apiHandler = new();

    public FileViewerModalTests()
    {
        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddLogging();
    }

    private static FileMetadataResponse Metadata(
        string viewMode,
        string extension = "xlsx",
        string? downloadOnlyReason = null,
        bool previewTruncated = false,
        string source = "ODrive") => new()
        {
            FileIndexId = FileIndexId,
            Name = $"Q3-<b>ledger</b>.{extension}",
            Extension = extension,
            SizeBytes = 482113,
            ModifiedAt = new DateTimeOffset(2026, 8, 14, 10, 22, 5, TimeSpan.Zero),
            Source = source,
            ViewMode = viewMode,
            DownloadOnlyReason = downloadOnlyReason,
            PreviewTruncated = previewTruncated
        };

    private void RespondWith(FileMetadataResponse metadata) =>
        _apiHandler.OnGetFileMetadata = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(metadata) });

    private void RespondWith(HttpStatusCode status) =>
        _apiHandler.OnGetFileMetadata = (_, _) => Task.FromResult(new HttpResponseMessage(status));

    private IRenderedComponent<FileViewerModal> RenderModal(string context = "KeywordSearch", Action? onClose = null)
    {
        var cut = RenderComponent<FileViewerModal>(p => p
            .Add(x => x.FileIndexId, FileIndexId)
            .Add(x => x.Context, context)
            .Add(x => x.OnClose, EventCallback.Factory.Create(this, onClose ?? (() => { }))));
        cut.WaitForState(() => !cut.Markup.Contains("Loading…"), TimeSpan.FromSeconds(3));
        return cut;
    }

    // -----------------------------------------------------------------------------------------
    // SC-14
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FileViewModes.InlinePdf, "pdf", false, false)]
    [InlineData(FileViewModes.InlineText, "txt", true, false)]
    [InlineData(FileViewModes.InlineText, "txt", true, true)]
    [InlineData(FileViewModes.ExtractedText, "doc", true, false)]
    [InlineData(FileViewModes.HtmlPreview, "docx", true, false)]
    [InlineData(FileViewModes.HtmlPreview, "xlsx", true, false)]
    public void Test_SC14_ModalRendersViewerPerViewMode(string viewMode, string extension, bool expectSandbox, bool truncated)
    {
        RespondWith(Metadata(viewMode, extension, previewTruncated: truncated, source: extension == "pdf" ? "B2" : "ODrive"));
        var closed = 0;

        var cut = RenderModal(context: "ODriveSync", onClose: () => closed++);

        // Metadata was requested through CachetApi with context forwarded.
        var apiRequest = _apiHandler.Requests.Should().ContainSingle().Subject;
        apiRequest.RequestUri!.PathAndQuery.Should().Be($"/api/files/{FileIndexId}?context=ODriveSync");

        // Dialog semantics (§8.4) and header (§8.3 point 2) — the file name is Razor-encoded.
        var dialog = cut.Find("[role=dialog]");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        dialog.GetAttribute("aria-labelledby").Should().Be("cachet-fv-title");
        cut.Find("#cachet-fv-title").TextContent.Should().Be($"Q3-<b>ledger</b>.{extension}");
        cut.Find("#cachet-fv-title").InnerHtml.Should().Be($"Q3-&lt;b&gt;ledger&lt;/b&gt;.{extension}", "the name is text, never markup");
        cut.Find(".cachet-fv-meta").TextContent.Should().Contain("482,113 bytes")
            .And.Contain("2026-08-14 10:22:05")
            .And.NotContain("UTC")
            .And.Contain(extension == "pdf" ? "B2 mirror" : "O-Drive mirror");

        // One iframe to Web's own pass-through view URL.
        var frame = cut.FindAll("iframe").Should().ContainSingle().Subject;
        frame.GetAttribute("src").Should().Be($"files/{FileIndexId}/view?context=ODriveSync");
        frame.GetAttribute("title").Should().NotBeNullOrWhiteSpace();
        if (expectSandbox)
        {
            frame.HasAttribute("sandbox").Should().BeTrue();
            frame.GetAttribute("sandbox").Should().BeEmpty("text/HTML modes use sandbox=\"\"");
        }
        else
        {
            frame.HasAttribute("sandbox").Should().BeFalse("InlinePdf has no sandbox — Chromium refuses sandboxed PDFs (§8.1)");
        }

        // Banners.
        cut.FindAll(".cachet-fv-truncated-banner").Should().HaveCount(truncated ? 1 : 0);
        if (truncated)
        {
            cut.Find(".cachet-fv-truncated-banner").TextContent.Should().Contain("Showing the first").And.Contain("Download the file for the full content.");
        }

        cut.FindAll(".cachet-fv-fidelity-banner").Should().HaveCount(viewMode == FileViewModes.HtmlPreview ? 1 : 0);
        if (viewMode == FileViewModes.HtmlPreview)
        {
            cut.Find(".cachet-fv-fidelity-banner").TextContent.Should().Be(
                "Read-only preview. Images, headers/footers, comments and tracked changes are not shown.");
        }

        cut.FindAll(".cachet-fv-text-only-banner").Should().HaveCount(viewMode == FileViewModes.ExtractedText ? 1 : 0);
        if (viewMode == FileViewModes.ExtractedText)
        {
            cut.Find(".cachet-fv-text-only-banner").TextContent.Should().Be("Text-only preview of a legacy Word (.doc) file.");
        }

        // §8.4: focus moves to Close on open.
        JSInterop.VerifyFocusAsyncInvoke().Arguments[0].Should().BeOfType<ElementReference>();

        // §8.3 point 5: Esc and Close both close; §8.3 point 7: FileIndexId -> null removes the iframe.
        dialog.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        closed.Should().Be(1);
        dialog.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        closed.Should().Be(1, "only Esc closes from the keyboard");
        cut.Find(".cachet-fv-close-btn").Click();
        closed.Should().Be(2);

        cut.SetParametersAndRender(p => p.Add(x => x.FileIndexId, (int?)null));
        cut.FindAll("iframe").Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();

        // Reopening issues a new metadata request.
        cut.SetParametersAndRender(p => p.Add(x => x.FileIndexId, (int?)FileIndexId));
        cut.WaitForState(() => cut.FindAll("iframe").Count == 1, TimeSpan.FromSeconds(3));
        _apiHandler.Requests.Should().HaveCount(2);
    }

    // -----------------------------------------------------------------------------------------
    // SC-15
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf", "TooLarge", "This file is too large to preview (limit 50 MB). Download it to view.")]
    [InlineData("docx", "TooLarge", "This file is too large to preview (limit 20 MB). Download it to view.")]
    [InlineData("xls", "SignatureMismatch", "This file's content does not match its .xls type, so it can't be previewed safely. You can still download it.")]
    public void Test_SC15_ModalDownloadOnlyAndErrorStates(string extension, string reason, string expectedMessage)
    {
        RespondWith(Metadata(FileViewModes.DownloadOnly, extension, downloadOnlyReason: reason));

        var cut = RenderModal();

        cut.Find(".cachet-fv-download-only").TextContent.Should().Be(expectedMessage);
        cut.FindAll("iframe").Should().BeEmpty("DownloadOnly never renders an iframe");
        cut.FindAll("a.cachet-fv-download").Should().ContainSingle("the Download button stays available");

        // Metadata errors: the §8.3 message; Download (plus the bold hint) only where the file exists
        // but could not be opened here (415 / other failures), never on 404 / 410.
        var errors = new (HttpStatusCode Status, string Message)[]
        {
            (HttpStatusCode.NotFound, "This file could not be found in the local mirror."),
            (HttpStatusCode.Gone, "This file is no longer in the archive."),
            (HttpStatusCode.UnsupportedMediaType, "This file type can't be opened here."),
            (HttpStatusCode.InternalServerError, "The file could not be opened. See the Audit Log or contact support.")
        };

        var nextId = FileIndexId + 1;
        foreach (var (status, message) in errors)
        {
            RespondWith(status);
            cut.SetParametersAndRender(p => p.Add(x => x.FileIndexId, (int?)nextId++));
            cut.WaitForState(() => cut.FindAll(".cachet-fv-error").Count == 1, TimeSpan.FromSeconds(3));

            cut.Find(".cachet-fv-error").TextContent.Should().Be(message, $"{(int)status}");
            if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                cut.FindAll("a.cachet-fv-download").Should().BeEmpty($"no Download link on a {(int)status} metadata error");
                cut.FindAll(".cachet-fv-download-hint").Should().BeEmpty();
            }
            else
            {
                cut.FindAll("a.cachet-fv-download").Should().ContainSingle($"Download offered on a {(int)status} metadata error");
                cut.Find(".cachet-fv-download-hint strong").TextContent.Should().Be(
                    "You can download the file and open it locally by clicking the Download button below.");
            }
            cut.FindAll("iframe").Should().BeEmpty();
        }

        // 401: the existing session-expired banner pattern, and no Download link.
        RespondWith(HttpStatusCode.Unauthorized);
        cut.SetParametersAndRender(p => p.Add(x => x.FileIndexId, (int?)nextId++));
        cut.WaitForState(() => cut.FindAll(".cachet-fv-error-banner").Count == 1, TimeSpan.FromSeconds(3));
        cut.Find(".cachet-fv-error-banner").TextContent.Should().Contain("Your session has expired");
        cut.FindAll("a.cachet-fv-download").Should().BeEmpty();

        // Api unreachable -> the generic message, Download still offered.
        _apiHandler.OnGetFileMetadata = (_, _) => throw new HttpRequestException("connection refused");
        cut.SetParametersAndRender(p => p.Add(x => x.FileIndexId, (int?)nextId++));
        cut.WaitForState(() => cut.FindAll(".cachet-fv-error").Count == 1, TimeSpan.FromSeconds(3));
        cut.Find(".cachet-fv-error").TextContent.Should().Be("The file could not be opened. See the Audit Log or contact support.");
        cut.FindAll("a.cachet-fv-download").Should().ContainSingle();

        // Malformed 200 body -> the generic message, Download still offered.
        _apiHandler.OnGetFileMetadata = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json")
        });
        cut.SetParametersAndRender(p => p.Add(x => x.FileIndexId, (int?)nextId++));
        cut.WaitForState(() => cut.FindAll(".cachet-fv-error").Count == 1, TimeSpan.FromSeconds(3));
        cut.FindAll("a.cachet-fv-download").Should().ContainSingle();
    }

    // -----------------------------------------------------------------------------------------
    // SC-16
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FileViewModes.HtmlPreview, "KeywordSearch")]
    [InlineData(FileViewModes.InlinePdf, "Quarantine")]
    [InlineData(FileViewModes.DownloadOnly, "ODriveSync")]
    public void Test_SC16_DownloadIsPlainNavigationNotCircuitStream(string viewMode, string context)
    {
        RespondWith(Metadata(viewMode, viewMode == FileViewModes.InlinePdf ? "pdf" : "xlsx",
            downloadOnlyReason: viewMode == FileViewModes.DownloadOnly ? "TooLarge" : null));

        var cut = RenderModal(context);

        var link = cut.Find("a.cachet-fv-download");
        link.GetAttribute("href").Should().Be($"files/{FileIndexId}/download?context={context}");
        link.HasAttribute("download").Should().BeTrue("a plain browser navigation with the download attribute");
        link.GetAttribute("class").Should().Contain("cachet-btn");
        link.HasAttribute("blazor:onclick").Should().BeFalse("no Blazor click handler — no bytes cross the SignalR circuit");

        // The only JS call is the §8.4 focus — never a download/stream helper (cf. CAS-032's
        // cachetDownload.fromStream).
        JSInterop.Invocations.Select(i => i.Identifier).Should().OnlyContain(id => id.Contains("focus", StringComparison.OrdinalIgnoreCase));
        JSInterop.Invocations.Should().NotContain(i => i.Identifier.Contains("download", StringComparison.OrdinalIgnoreCase)
            || i.Identifier.Contains("stream", StringComparison.OrdinalIgnoreCase));

        // Only the metadata call went to Api — no file bytes were fetched over the circuit.
        _apiHandler.Requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath.Should().Be($"/api/files/{FileIndexId}");
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
