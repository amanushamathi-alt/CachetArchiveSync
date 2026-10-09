using System.Text;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-040 §13 — <see cref="OfficePreviewRenderer"/> (SC-5 DOCX encoded structural HTML, SC-6
/// spreadsheet cached values with caps, SC-7 legacy DOC extracted text) and the V9 preview refusals
/// of <see cref="Services.FileContentService.GetViewAsync"/> (<c>TooLarge</c>,
/// <c>SignatureMismatch</c>, <c>ConversionFailed</c>, <c>Timeout</c> via a fake slow renderer,
/// <c>ArchiveGuard</c> via a generated zip bomb). All fixtures are generated at test time except
/// the committed <c>TestAssets/legacy-word-sample.doc</c>.
/// </summary>
public sealed class OfficePreviewRendererTests
{
    public OfficePreviewRendererTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static OfficePreviewRenderer Renderer(FilePreviewOptions? options = null) =>
        new(Options.Create(options ?? new FilePreviewOptions()));

    private static string Html(OfficePreview preview) => Encoding.UTF8.GetString(preview.Body);

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_DocxPreviewIsEncodedStructuralHtml()
    {
        var docx = FileViewerTestFixtures.Docx();

        var preview = Renderer().RenderDocx(docx);
        var html = Html(preview);

        preview.Truncated.Should().BeFalse();

        // Paragraph and table text, in document order; Heading 1 -> h1, bold/italic -> strong/em.
        html.Should().Contain("<h1>Quarterly Heading</h1>");
        html.Should().Contain("<strong>BoldPart</strong>");
        html.Should().Contain("<em>ItalicPart</em>");
        html.Should().Contain("<table>").And.Contain("<td>CellA1</td>");
        var order = new[] { "Quarterly Heading", "BoldPart", "ItalicPart", "alert(1)", "CellA1", "CellB1", "CellA2", "CellB2", "Closing paragraph" }
            .Select(t => html.IndexOf(t, StringComparison.Ordinal))
            .ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder("paragraph and table text is rendered in document order");

        // The <script> run appears only HTML-encoded; no active markup at all.
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().NotContainEquivalentOf("<script");
        html.Should().NotContainEquivalentOf("<img");
        html.Should().NotContainEquivalentOf("<a ");
        html.Should().NotContainEquivalentOf("href=");
        html.Should().NotContainEquivalentOf("src=");

        // Through /view: text/html, HtmlPreview.
        var harness = new FileContentServiceHarness();
        harness.Seed(FileViewerTestFixtures.Record(fileIndexId: 900, extension: "docx"), docx);
        var result = await harness.Build().GetViewAsync(900, "KeywordSearch", 1, CancellationToken.None);
        result.Outcome.Should().Be(FileContentOutcome.Success);
        result.ContentType.Should().Be("text/html; charset=utf-8");
        result.ViewMode.Should().Be(FileViewModes.HtmlPreview);
        result.PreviewTruncated.Should().BeFalse();
        Encoding.UTF8.GetString(await FileContentServiceHarness.ReadAllAsync(result.Content!)).Should().Be(html);

        // MaxHtmlBytes cap -> truncation banner.
        var capped = Renderer(new FilePreviewOptions { MaxHtmlBytes = 60 }).RenderDocx(docx);
        capped.Truncated.Should().BeTrue();
        Html(capped).Should().Contain("Preview truncated").And.NotContain("Closing paragraph");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Test_SC6_SpreadsheetPreviewRendersCachedValuesWithCaps(bool isXlsx)
    {
        var workbook = FileViewerTestFixtures.Workbook(isXlsx, extraRows: 5);

        var preview = Renderer().RenderSpreadsheet(workbook, isXlsx);
        var html = Html(preview);

        // Every sheet, the hidden one labelled.
        html.Should().Contain("<h2 class=\"sheet\">Data</h2>");
        html.Should().Contain("<h2 class=\"sheet\">Secret (hidden)</h2>");
        html.Should().Contain("hidden-value");
        html.Should().Contain("<h2 class=\"sheet\">Empty</h2>");

        // Cached formula values, never formula text or a re-evaluation (A1 changed to 100 after caching).
        html.Should().Contain("<td>5</td>", "C1's cached A1+B1 result");
        html.Should().NotContain("103", "formulas are never re-evaluated (§12)");
        html.Should().NotContain("A1+B1");
        html.Should().Contain("<td>xy</td>");
        html.Should().Contain("<td>TRUE</td>");
        html.Should().Contain("<td>#DIV/0!</td>");
        html.Should().Contain("&lt;b&gt;bold?&lt;/b&gt;", "cell text is HTML-encoded");
        html.Should().Contain("<th>A</th>").And.Contain("<th>1</th>");
        preview.Truncated.Should().BeFalse();
        html.Should().NotContain("Preview truncated");

        // Row / column / sheet caps -> truncated with a banner.
        var capped = Renderer(new FilePreviewOptions { MaxRowsPerSheet = 3, MaxColumns = 2, MaxSheets = 1 })
            .RenderSpreadsheet(workbook, isXlsx);
        var cappedHtml = Html(capped);
        capped.Truncated.Should().BeTrue();
        cappedHtml.Should().Contain("Preview truncated");
        cappedHtml.Should().Contain("row3").And.NotContain("row4", "rows beyond MaxRowsPerSheet are dropped");
        cappedHtml.Should().Contain("<th>B</th>").And.NotContain("<th>C</th>", "columns beyond MaxColumns are dropped");
        cappedHtml.Should().NotContain("Secret", "sheets beyond MaxSheets are dropped");

        // Through /view: HtmlPreview, text/html.
        var extension = isXlsx ? "xlsx" : "xls";
        var harness = new FileContentServiceHarness();
        harness.Seed(FileViewerTestFixtures.Record(fileIndexId: 901, extension: extension), workbook);
        var result = await harness.Build().GetViewAsync(901, "ODriveSync", 1, CancellationToken.None);
        result.Outcome.Should().Be(FileContentOutcome.Success);
        result.ContentType.Should().Be("text/html; charset=utf-8");
        result.ViewMode.Should().Be(FileViewModes.HtmlPreview);
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC7_LegacyDocViewReturnsExtractedText()
    {
        var doc = await File.ReadAllBytesAsync(FileViewerTestFixtures.LegacyDocPath);
        var expected = ExtractWithB2xTranslator(doc);
        expected.Should().Contain("This is a simple file created with Word 97-SR2");

        var harness = new FileContentServiceHarness();
        harness.Seed(FileViewerTestFixtures.Record(fileIndexId: 902, extension: "doc"), doc);
        var result = await harness.Build().GetViewAsync(902, "Quarantine", 1, CancellationToken.None);

        result.Outcome.Should().Be(FileContentOutcome.Success);
        result.ContentType.Should().Be("text/plain; charset=utf-8");
        result.ViewMode.Should().Be(FileViewModes.ExtractedText);
        result.PreviewTruncated.Should().BeFalse();
        Encoding.UTF8.GetString(await FileContentServiceHarness.ReadAllAsync(result.Content!)).Should().Be(expected);

        // Capped at TextMaxBytes, on a character boundary.
        var capped = Renderer(new FilePreviewOptions { TextMaxBytes = 10 }).ExtractLegacyDocText(doc);
        capped.Truncated.Should().BeTrue();
        Encoding.UTF8.GetString(capped.Body).Should().Be(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(expected!)[..10]));
    }

    // -----------------------------------------------------------------------------------------
    // V9
    // -----------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> V9Cases => new()
    {
        // reason, extension, fixture
        { "TooLarge", "pdf", "pdf-over-cap" },
        { "TooLarge", "docx", "docx-over-cap" },
        { "SignatureMismatch", "pdf", "renamed-html" },
        { "SignatureMismatch", "xls", "renamed-html" },
        { "SignatureMismatch", "doc", "renamed-html" },
        { "ConversionFailed", "docx", "corrupt-zip" },
        { "ConversionFailed", "xls", "corrupt-ole2" },
        { "ConversionFailed", "xlsx", "plain-zip" },
        { "ArchiveGuard", "docx", "zip-bomb" },
        { "ArchiveGuard", "xlsx", "zip-over-total" },
        { "Timeout", "docx", "slow-renderer" },
        { "Timeout", "xlsx", "limiter-saturated" }
    };

    [Theory]
    [MemberData(nameof(V9Cases))]
    public async Task Test_V9_PreviewRefusedForOversizeMismatchOrFailedConversion(string reason, string extension, string fixture)
    {
        var harness = new FileContentServiceHarness();
        byte[] bytes;
        PreviewConversionLimiterBlocker? blocker = null;
        switch (fixture)
        {
            case "pdf-over-cap":
                bytes = FileViewerTestFixtures.Pdf();
                harness.Options.PdfMaxBytes = bytes.Length - 1;
                break;
            case "docx-over-cap":
                bytes = FileViewerTestFixtures.Docx();
                harness.Options.OfficeMaxBytes = bytes.Length - 1;
                break;
            case "renamed-html":
                bytes = FileViewerTestFixtures.RenamedHtmlPdf();
                break;
            case "corrupt-zip":
                bytes = FileViewerTestFixtures.CorruptZip();
                break;
            case "corrupt-ole2":
                bytes = FileViewerTestFixtures.CorruptOle2();
                break;
            case "plain-zip":
                // A readable ZIP that passes the archive guard but is not a workbook — NPOI throws.
                bytes = FileViewerTestFixtures.PlainZip(256);
                break;
            case "zip-bomb":
                bytes = FileViewerTestFixtures.ZipBomb();
                break;
            case "zip-over-total":
                bytes = FileViewerTestFixtures.PlainZip(4096);
                harness.Options.MaxUncompressedBytes = 1024;
                break;
            case "slow-renderer":
                {
                    bytes = FileViewerTestFixtures.Docx();
                    harness.Options.TimeoutSeconds = 1;
                    var slow = Substitute.For<IOfficePreviewRenderer>();
                    slow.RenderDocx(Arg.Any<byte[]>()).Returns(_ =>
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(3));
                        return new OfficePreview(Array.Empty<byte>(), false);
                    });
                    harness.RendererOverride = slow;
                    break;
                }
            default: // limiter-saturated: the only conversion slot is held, so waiting times out.
                bytes = FileViewerTestFixtures.Workbook(isXlsx: true);
                harness.Options.TimeoutSeconds = 1;
                harness.Options.MaxConcurrentConversions = 1;
                blocker = new PreviewConversionLimiterBlocker();
                harness.RendererOverride = blocker.Renderer;
                break;
        }

        harness.Seed(FileViewerTestFixtures.Record(fileIndexId: 950, extension: extension), bytes);
        var sut = harness.Build();

        if (blocker is not null)
        {
            // Occupy the single slot with a first, never-finishing conversion.
            var first = sut.GetViewAsync(950, "KeywordSearch", 1, CancellationToken.None);
            blocker.Started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            (await first).PreviewReason.Should().Be("Timeout");
            harness.AuditLogService.ClearReceivedCalls();
        }

        var result = await sut.GetViewAsync(950, "KeywordSearch", 1, CancellationToken.None);
        blocker?.Release.Set();

        result.Outcome.Should().Be(FileContentOutcome.PreviewNotAvailable);
        result.PreviewReason.Should().Be(reason);
        result.Content.Should().BeNull();

        var entry = harness.AuditEntries.Should().ContainSingle("a refused preview is audited as Failure").Subject;
        entry.Action.Should().Be(AuditAction.FileViewed);
        entry.Result.Should().Be(AuditResult.Failure);
        entry.ResultDetail.Should().Be($"fileIndexId=950; reason={reason}");

        // /download is unaffected by every V9 condition.
        var download = await sut.GetDownloadAsync(950, "KeywordSearch", 1, CancellationToken.None);
        download.Outcome.Should().Be(FileContentOutcome.Success);
        (await FileContentServiceHarness.ReadAllAsync(download.Content!)).Should().Equal(bytes);
        await download.Content!.DisposeAsync();
    }

    /// <summary>
    /// The same b2xtranslator calls <c>WordTextExtractor</c> (internal to the Api assembly) makes for
    /// a legacy <c>.doc</c> — the expected text SC-7 compares against.
    /// </summary>
    private static string ExtractWithB2xTranslator(byte[] doc)
    {
        using var stream = new MemoryStream(doc, writable: false);
        using var reader = new b2xtranslator.StructuredStorage.Reader.StructuredStorageReader(stream, logger: null);
        var legacyDocument = new b2xtranslator.DocFileFormat.WordDocument(reader);
        var textDocument = b2xtranslator.txt.TextModel.TextDocument.Create(string.Empty, null, extractUrls: true);
        return b2xtranslator.txt.DocTextExtractor.ConvertToString(legacyDocument, textDocument, extractUrls: true);
    }

    /// <summary>A renderer whose spreadsheet conversion blocks until released.</summary>
    private sealed class PreviewConversionLimiterBlocker
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public IOfficePreviewRenderer Renderer { get; }

        public PreviewConversionLimiterBlocker()
        {
            Renderer = Substitute.For<IOfficePreviewRenderer>();
            Renderer.RenderSpreadsheet(Arg.Any<byte[]>(), Arg.Any<bool>()).Returns(_ =>
            {
                Started.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
                return new OfficePreview(Array.Empty<byte>(), false);
            });
        }
    }
}
