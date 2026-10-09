using System.Text;
using Cachet.ArchiveSync.Api.Content;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="FileContentMatcher"/> (CAS-012 §5/§14) — the concrete
/// <see cref="Cachet.ArchiveSync.Core.Interfaces.IFileContentMatcher"/> implementation dispatching
/// per file extension to one <c>ITextExtractor</c> (internal, so exercised here only through the
/// public <c>TryMatchAsync</c> surface — CAS-012 §13's "concrete IFileContentMatcher implementation's
/// own test class," authored here since the implementer already built the concrete class this
/// phase). Proves the match/no-match/bounded-snippet contract (§7 V11/V12) and the extraction-failure
/// skip path (§7 V10) directly, in addition to the end-to-end coverage already exercised through
/// <c>KeywordSearchTaskProcessorTests</c>.
/// </summary>
public sealed class FileContentMatcherTests
{
    private static FileContentMatcher CreateSut(out ILogger<FileContentMatcher> logger)
    {
        logger = Substitute.For<ILogger<FileContentMatcher>>();
        return new FileContentMatcher(logger);
    }

    private static List<string> ExtractLoggedMessages(ILogger logger)
    {
        var messages = new List<string>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    // ---- .txt: plain-read extractor, no external library involved ----------------------------------

    [Fact]
    public async Task Test_TryMatchAsync_TxtContainingTerm_ReturnsBoundedSnippet()
    {
        // Arrange
        var sut = CreateSut(out _);
        var prefix = new string('a', 500);
        var suffix = new string('b', 500);
        var text = prefix + " the confidential settlement clause is here " + suffix;
        var bytes = Encoding.UTF8.GetBytes(text);

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "txt", "confidential settlement", CancellationToken.None);

        // Assert — V12: bounded, not the full ~1050-character file.
        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
        snippet.Length.Should().BeLessThan(300);
        snippet.Should().NotContain(new string('a', 200));
        snippet.Should().NotContain(new string('b', 200));
    }

    [Fact]
    public async Task Test_TryMatchAsync_TxtCaseInsensitiveMatch_ReturnsSnippet()
    {
        // Arrange
        var sut = CreateSut(out _);
        var bytes = Encoding.UTF8.GetBytes("This mentions a CONFIDENTIAL SETTLEMENT in upper case.");

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "txt", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().NotBeNull();
    }

    [Fact]
    public async Task Test_TryMatchAsync_TxtNotContainingTerm_ReturnsNull()
    {
        // Arrange — §7 V11: no match means null, dropped by the caller before any B2 cross-check.
        var sut = CreateSut(out _);
        var bytes = Encoding.UTF8.GetBytes("Nothing relevant to the search term is present in this file.");

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "txt", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().BeNull();
    }

    [Fact]
    public async Task Test_TryMatchAsync_EmptyFile_ReturnsNull()
    {
        // Arrange — §7 V11/§14: a scanned/image-only file's extracted text can be empty; treated
        // identically to a non-match, never an error.
        var sut = CreateSut(out _);

        // Act
        var snippet = await sut.TryMatchAsync(Array.Empty<byte>(), "txt", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().BeNull();
    }

    [Fact]
    public async Task Test_TryMatchAsync_UnknownExtension_ReturnsNullWithoutLogging()
    {
        // Arrange — a file type this matcher has no extractor for is treated as a skip, not a
        // failure (§7 V10), and never even attempts extraction.
        var sut = CreateSut(out var logger);
        var bytes = Encoding.UTF8.GetBytes("irrelevant");

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "zip", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().BeNull();
        ExtractLoggedMessages(logger).Should().BeEmpty();
    }

    // ---- .docx: NPOI (XWPFDocument) round trip -----------------------------------------------------

    [Fact]
    public async Task Test_TryMatchAsync_DocxContainingTerm_ReturnsSnippet()
    {
        // Arrange — a real, valid .docx built with NPOI (the same library FileContentMatcher's
        // WordTextExtractor uses), proving the dispatch-by-extension + real extraction path, not
        // just the plain-text branch.
        var sut = CreateSut(out _);
        using var document = new XWPFDocument();
        var paragraph = document.CreateParagraph();
        var run = paragraph.CreateRun();
        run.SetText("Meeting notes regarding the confidential settlement reached last quarter.");
        using var stream = new MemoryStream();
        document.Write(stream);
        var bytes = stream.ToArray();

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "docx", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
    }

    [Fact]
    public async Task Test_TryMatchAsync_DocxNotContainingTerm_ReturnsNull()
    {
        // Arrange
        var sut = CreateSut(out _);
        using var document = new XWPFDocument();
        var paragraph = document.CreateParagraph();
        var run = paragraph.CreateRun();
        run.SetText("Nothing relevant here.");
        using var stream = new MemoryStream();
        document.Write(stream);
        var bytes = stream.ToArray();

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "docx", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().BeNull();
    }

    // ---- .xlsx: NPOI (XSSFWorkbook) round trip -------------------------------------------------------

    [Fact]
    public async Task Test_TryMatchAsync_XlsxContainingTerm_ReturnsSnippet()
    {
        // Arrange
        var sut = CreateSut(out _);
        using var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Sheet1");
        var row = sheet.CreateRow(0);
        row.CreateCell(0).SetCellValue("confidential settlement amount: 250000");
        using var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        var bytes = stream.ToArray();

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "xlsx", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
    }

    // ---- .xls (legacy binary Excel): NPOI (HSSFWorkbook) round trip --------------------------------

    [Fact]
    public async Task Test_TryMatchAsync_XlsContainingTerm_ReturnsSnippet()
    {
        // Arrange — a real, valid legacy .xls built with NPOI's HSSFWorkbook (the same library
        // ExcelTextExtractor uses for this format — unlike .doc, NPOI's HSSF module fully supports
        // the legacy binary Excel format, so no second library is needed here).
        var sut = CreateSut(out _);
        using var workbook = new HSSFWorkbook();
        var sheet = workbook.CreateSheet("Sheet1");
        var row = sheet.CreateRow(0);
        row.CreateCell(0).SetCellValue("confidential settlement amount: 250000");
        using var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        var bytes = stream.ToArray();

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "xls", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
    }

    // ---- .pdf: PdfOxide round trip, PDF authored with PdfSharp (already used for reporting) --------

    [Fact]
    public async Task Test_TryMatchAsync_PdfContainingTerm_ReturnsSnippet()
    {
        // Arrange — a real, valid PDF built with PdfSharp (already a dependency for report
        // generation, CAS-010), proving PdfTextExtractor's PdfOxide-based read against real PDF
        // bytes rather than only the corrupt-bytes error path.
        var sut = CreateSut(out _);
        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
        var font = new PdfSharp.Drawing.XFont("Verdana", 12);
        gfx.DrawString(
            "Meeting notes regarding the confidential settlement reached last quarter.",
            font,
            PdfSharp.Drawing.XBrushes.Black,
            new PdfSharp.Drawing.XRect(20, 20, page.Width.Point - 40, page.Height.Point - 40),
            PdfSharp.Drawing.XStringFormats.TopLeft);
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        var bytes = stream.ToArray();

        // Act
        var snippet = await sut.TryMatchAsync(bytes, "pdf", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
    }

    // ---- .doc (legacy binary Word): GHSoftware.WordDocTextExtractor round trip ---------------------

    [Fact]
    public async Task Test_TryMatchAsync_DocContainingTerm_ReturnsSnippet()
    {
        // Arrange — no library in this solution can author a legacy binary .doc file (NPOI's HWPF
        // module was never implemented for .NET — CAS-012 §14's WordTextExtractor comment), so this
        // test reads a real, small, known-content legacy .doc fixture instead of building one
        // in-memory. The fixture is one of the public test-corpus files from the
        // GHSoftware.WordDocTextExtractor (b2xtranslator) project itself — see TestAssets/README.md
        // for provenance — chosen specifically to prove the new library's happy path, not just its
        // corrupt-bytes error path (already covered above).
        var sut = CreateSut(out _);
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "legacy-word-sample.doc");
        var bytes = await File.ReadAllBytesAsync(fixturePath);

        // Act — the fixture's known text is "This is a simple file created with Word 97-SR2."
        var snippet = await sut.TryMatchAsync(bytes, "doc", "simple file", CancellationToken.None);

        // Assert
        snippet.Should().NotBeNull();
        snippet!.Should().Contain("simple file");
    }

    // ---- Extraction failure: caught, logged as a fixed warning, never the file bytes/term ----------

    [Fact]
    public async Task Test_TryMatchAsync_CorruptDocxBytes_ReturnsNullAndLogsFixedWarningOnly()
    {
        // Arrange — bytes that are not a valid OOXML package at all; NPOI's XWPFDocument
        // constructor throws, which FileContentMatcher must catch (§7 V10 — a skip, not a task
        // failure) and log only a short, fixed warning naming the extension (§11 — never the raw
        // bytes or the search term).
        var sut = CreateSut(out var logger);
        const string term = "acme-corp-merger-confidential-code-7734";
        var corruptBytes = Encoding.UTF8.GetBytes("this is not a valid docx package");

        // Act
        var snippet = await sut.TryMatchAsync(corruptBytes, "docx", term, CancellationToken.None);

        // Assert
        snippet.Should().BeNull();
        var messages = ExtractLoggedMessages(logger);
        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(m =>
            !m.Contains(term, StringComparison.Ordinal) &&
            !m.Contains("this is not a valid docx package", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains("docx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Test_TryMatchAsync_CorruptPdfBytes_ReturnsNullAndLogsFixedWarningOnly()
    {
        // Arrange — PdfOxide throws on bytes that are not a valid PDF; same skip/log-hygiene
        // contract as the .docx corrupt-bytes case above, for a second extractor.
        var sut = CreateSut(out var logger);
        var corruptBytes = Encoding.UTF8.GetBytes("this is not a valid pdf");

        // Act
        var snippet = await sut.TryMatchAsync(corruptBytes, "pdf", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().BeNull();
        ExtractLoggedMessages(logger).Should().Contain(m => m.Contains("pdf", StringComparison.OrdinalIgnoreCase));
    }

    // ---- TryMatchStreamAsync: chunked scan of plain-text files ---------------------------------------

    [Theory]
    [InlineData(10)]          // match early in the first chunk
    [InlineData(65520)]       // match straddles the 64K chunk boundary
    [InlineData(200000)]      // match several chunks in
    public async Task Test_TryMatchStreamAsync_Txt_SameSnippetAsWholeFileMatch(int matchOffset)
    {
        // Arrange
        var sut = CreateSut(out _);
        const string term = "confidential settlement";
        var text = new string('a', matchOffset) + term + new string('b', 400);
        var bytes = Encoding.UTF8.GetBytes(text);

        // Act
        var streamed = await sut.TryMatchStreamAsync(new MemoryStream(bytes), "txt", term, CancellationToken.None);
        var whole = await sut.TryMatchAsync(bytes, "txt", term, CancellationToken.None);

        // Assert
        streamed.Should().NotBeNull();
        streamed.Should().Be(whole);
    }

    [Fact]
    public async Task Test_TryMatchStreamAsync_TermAbsent_ReturnsNull()
    {
        var sut = CreateSut(out _);
        var bytes = Encoding.UTF8.GetBytes(new string('a', 200000));

        var snippet = await sut.TryMatchStreamAsync(new MemoryStream(bytes), "csv", "confidential", CancellationToken.None);

        snippet.Should().BeNull();
    }

    [Fact]
    public async Task Test_TryMatchStreamAsync_NonPlainTextExtension_ReturnsNull()
    {
        var sut = CreateSut(out _);
        var bytes = Encoding.UTF8.GetBytes("confidential");

        var snippet = await sut.TryMatchStreamAsync(new MemoryStream(bytes), "pdf", "confidential", CancellationToken.None);

        snippet.Should().BeNull();
    }

    // ---- format detection: a binary .xls saved as .xlsx is still searchable --------------------------

    [Fact]
    public async Task Test_TryMatchAsync_BinaryXlsNamedXlsx_StillMatches()
    {
        // Arrange
        var sut = CreateSut(out var logger);
        using var workbook = new HSSFWorkbook();
        workbook.CreateSheet("S").CreateRow(0).CreateCell(0).SetCellValue("confidential settlement ledger");
        using var ms = new MemoryStream();
        workbook.Write(ms, leaveOpen: true);

        // Act — handed over with the wrong extension, as in the archive.
        var snippet = await sut.TryMatchAsync(ms.ToArray(), "xlsx", "confidential settlement", CancellationToken.None);

        // Assert
        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
        ExtractLoggedMessages(logger).Should().BeEmpty();
    }

    // ---- .xlsx streaming reader (flat memory, early exit) --------------------------------------------

    private static byte[] BuildXlsx(Action<ISheet> fill)
    {
        using var workbook = new XSSFWorkbook();
        fill(workbook.CreateSheet("S"));
        using var ms = new MemoryStream();
        workbook.Write(ms, leaveOpen: true);
        return ms.ToArray();
    }

    [Fact]
    public async Task Test_TryMatchAsync_XlsxSharedString_Matches()
    {
        var sut = CreateSut(out var logger);
        var bytes = BuildXlsx(s => s.CreateRow(0).CreateCell(0).SetCellValue("the confidential settlement ledger"));

        var snippet = await sut.TryMatchAsync(bytes, "xlsx", "confidential settlement", CancellationToken.None);

        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
        ExtractLoggedMessages(logger).Should().BeEmpty();
    }

    [Fact]
    public async Task Test_TryMatchAsync_XlsxNumericCell_MatchesStoredValue()
    {
        var sut = CreateSut(out _);
        var bytes = BuildXlsx(s => s.CreateRow(0).CreateCell(0).SetCellValue(987654321));

        var snippet = await sut.TryMatchAsync(bytes, "xlsx", "987654321", CancellationToken.None);

        snippet.Should().NotBeNull();
    }

    [Fact]
    public async Task Test_TryMatchAsync_XlsxTermAbsent_ReturnsNullWithoutWarning()
    {
        var sut = CreateSut(out var logger);
        var bytes = BuildXlsx(s => s.CreateRow(0).CreateCell(0).SetCellValue("nothing relevant"));

        var snippet = await sut.TryMatchAsync(bytes, "xlsx", "confidential", CancellationToken.None);

        snippet.Should().BeNull();
        ExtractLoggedMessages(logger).Should().BeEmpty();
    }

    [Fact]
    public async Task Test_TryMatchAsync_XlsxSharedStringIndexIsNotSearchedAsContent()
    {
        // Cell A1 holds the string "alpha" (shared-string index 0); searching "0" must not hit the index.
        var sut = CreateSut(out _);
        var bytes = BuildXlsx(s => s.CreateRow(0).CreateCell(0).SetCellValue("alpha"));

        var snippet = await sut.TryMatchAsync(bytes, "0", "zzz", CancellationToken.None);
        var viaXlsx = await sut.TryMatchAsync(bytes, "xlsx", "zzz", CancellationToken.None);

        snippet.Should().BeNull();
        viaXlsx.Should().BeNull();
    }

    [Fact]
    public async Task Test_TryMatchAsync_XlsxLargeSparseSheet_FindsMatchQuickly()
    {
        // 200k populated rows; the match is in row 0, so the streaming reader must stop early.
        var sut = CreateSut(out _);
        var bytes = BuildXlsx(s =>
        {
            s.CreateRow(0).CreateCell(0).SetCellValue("pioneer payroll");
            for (var r = 1; r < 20000; r++)
            {
                s.CreateRow(r).CreateCell(0).SetCellValue("row " + r);
            }
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var snippet = await sut.TryMatchAsync(bytes, "xlsx", "pioneer", CancellationToken.None);
        watch.Stop();

        snippet.Should().NotBeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // ---- damaged legacy binary files: raw-byte fallback + one-line logging ---------------------------

    [Fact]
    public async Task Test_TryMatchAsync_DamagedXlsWithLatin1Text_StillMatchesViaRawScan()
    {
        var sut = CreateSut(out var logger);
        var bytes = Encoding.ASCII.GetBytes("  garbage   the confidential settlement ledger    more");

        var snippet = await sut.TryMatchAsync(bytes, "xls", "confidential settlement", CancellationToken.None);

        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
        snippet.Should().NotContain(" ");
        ExtractLoggedMessages(logger).Should().BeEmpty();
    }

    [Fact]
    public async Task Test_TryMatchAsync_DamagedDocWithUtf16Text_StillMatchesViaRawScan()
    {
        var sut = CreateSut(out _);
        var bytes = new byte[] { 1, 2, 3 }
            .Concat(Encoding.Unicode.GetBytes("the confidential settlement clause"))
            .Concat(new byte[] { 9, 9 }).ToArray();

        var snippet = await sut.TryMatchAsync(bytes, "doc", "confidential settlement", CancellationToken.None);

        snippet.Should().NotBeNull();
        snippet!.Should().Contain("confidential settlement");
    }

    [Fact]
    public async Task Test_TryMatchAsync_DamagedXlsWithoutTerm_LogsOneLineWithoutStackTrace()
    {
        var sut = CreateSut(out var logger);

        var snippet = await sut.TryMatchAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "xls", "confidential", CancellationToken.None);

        snippet.Should().BeNull();
        var messages = ExtractLoggedMessages(logger);
        messages.Should().ContainSingle();
        messages[0].Should().NotContain("   at ");
        messages[0].Length.Should().BeLessThan(700);
    }

    // ---- Excel 5.0/95 (BIFF5) workbooks: OLE stream "Book", 8-bit strings --------------------------

    private static byte[] BuildBiff5Xls(string streamName, params (string Label, double Number)[] cells)
    {
        static void Record(MemoryStream ms, int id, byte[] body)
        {
            ms.WriteByte((byte)(id & 0xFF)); ms.WriteByte((byte)(id >> 8));
            ms.WriteByte((byte)(body.Length & 0xFF)); ms.WriteByte((byte)(body.Length >> 8));
            ms.Write(body);
        }

        using var book = new MemoryStream();
        Record(book, 0x0809, new byte[] { 0x00, 0x05, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00 }); // BOF: BIFF5, workbook globals
        var row = 0;
        foreach (var (label, number) in cells)
        {
            var text = Encoding.Latin1.GetBytes(label);
            var labelBody = new byte[8 + text.Length];
            labelBody[0] = (byte)row;                  // row
            labelBody[6] = (byte)text.Length;          // cch (low byte)
            Array.Copy(text, 0, labelBody, 8, text.Length);
            Record(book, 0x0204, labelBody);           // LABEL

            var numberBody = new byte[14];
            numberBody[0] = (byte)row;
            BitConverter.GetBytes(number).CopyTo(numberBody, 6);
            Record(book, 0x0203, numberBody);          // NUMBER
            row++;
        }

        Record(book, 0x000A, Array.Empty<byte>());     // EOF

        var fs = new NPOI.POIFS.FileSystem.POIFSFileSystem();
        fs.CreateDocument(new MemoryStream(book.ToArray()), streamName);
        using var ole = new MemoryStream();
        fs.WriteFileSystem(ole);
        return ole.ToArray();
    }

    [Theory]
    [InlineData("Book")]
    [InlineData("BOOK")]
    public async Task Test_TryMatchAsync_Biff5Xls_TextAndNumbersAreSearchable(string streamName)
    {
        var sut = CreateSut(out var logger);
        var bytes = BuildBiff5Xls(streamName, ("dsettle", 1), ("accuserv payroll", 149430.91));

        var text = await sut.TryMatchAsync(bytes, "xls", "accuserv payroll", CancellationToken.None);
        var number = await sut.TryMatchAsync(bytes, "xls", "149430.91", CancellationToken.None);

        text.Should().NotBeNull();
        text!.Should().Contain("accuserv payroll");
        number.Should().NotBeNull();
        ExtractLoggedMessages(logger).Should().BeEmpty();
    }

    [Fact]
    public async Task Test_TryMatchAsync_Biff5XlsTermAbsent_ReturnsNullWithoutWarning()
    {
        var sut = CreateSut(out var logger);
        var bytes = BuildBiff5Xls("Book", ("dsettle", 1));

        var snippet = await sut.TryMatchAsync(bytes, "xls", "confidential", CancellationToken.None);

        snippet.Should().BeNull();
        ExtractLoggedMessages(logger).Should().BeEmpty();
    }
}
