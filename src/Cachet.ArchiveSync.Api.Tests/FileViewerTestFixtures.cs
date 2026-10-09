using System.IO.Compression;
using System.Text;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using NSubstitute;
using PdfSharp.Pdf;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-040 §13 fixtures — every file is generated at test time (PdfSharp PDF, NPOI DOCX/XLSX/XLS,
/// TXT byte arrays, a <see cref="System.IO.Compression"/> zip bomb, a renamed-HTML ".pdf"). The only
/// committed binary is the existing <c>TestAssets/legacy-word-sample.doc</c>.
/// </summary>
internal static class FileViewerTestFixtures
{
    public const string ScriptRun = "<script>alert(1)</script>";

    public static readonly string LegacyDocPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", "legacy-word-sample.doc");

    public static byte[] Pdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    /// <summary>An HTML document renamed to ".pdf" (§12 MIME-confusion threat).</summary>
    public static byte[] RenamedHtmlPdf() =>
        Encoding.UTF8.GetBytes("<html><body><script>alert('x')</script></body></html>");

    public static byte[] Docx()
    {
        using var document = new XWPFDocument();

        var heading = document.CreateParagraph();
        heading.Style = "Heading1";
        heading.CreateRun().SetText("Quarterly Heading");

        var paragraph = document.CreateParagraph();
        var bold = paragraph.CreateRun();
        bold.SetText("BoldPart");
        bold.IsBold = true;
        var italic = paragraph.CreateRun();
        italic.SetText("ItalicPart");
        italic.IsItalic = true;
        paragraph.CreateRun().SetText(ScriptRun);

        var table = document.CreateTable(2, 2);
        table.GetRow(0).GetCell(0).SetText("CellA1");
        table.GetRow(0).GetCell(1).SetText("CellB1");
        table.GetRow(1).GetCell(0).SetText("CellA2");
        table.GetRow(1).GetCell(1).SetText("CellB2");

        document.CreateParagraph().CreateRun().SetText("Closing paragraph");

        using var stream = new MemoryStream();
        document.Write(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// A workbook with: sheet "Data" (A1=2, B1=3, C1 = A1+B1 cached as 5 — A1 is then changed to 100
    /// without re-evaluation, so a re-evaluating renderer would show 103), string/boolean/error
    /// formulas with cached results, a hidden sheet "Secret", an empty sheet, and
    /// <paramref name="extraRows"/> additional rows on "Data" for cap tests.
    /// </summary>
    public static byte[] Workbook(bool isXlsx, int extraRows = 0)
    {
        using IWorkbook workbook = isXlsx ? new XSSFWorkbook() : new HSSFWorkbook();
        var data = workbook.CreateSheet("Data");
        var row0 = data.CreateRow(0);
        row0.CreateCell(0).SetCellValue(2);
        row0.CreateCell(1).SetCellValue(3);
        row0.CreateCell(2).SetCellFormula("A1+B1");
        row0.CreateCell(3).SetCellFormula("\"x\"&\"y\"");
        row0.CreateCell(4).SetCellFormula("1=1");
        row0.CreateCell(5).SetCellFormula("1/0");
        row0.CreateCell(6).SetCellValue("<b>bold?</b>");

        workbook.GetCreationHelper().CreateFormulaEvaluator().EvaluateAll();

        // Change an input AFTER caching: the cached C1 value stays 5.
        row0.GetCell(0).SetCellValue(100);

        for (var r = 1; r <= extraRows; r++)
        {
            data.CreateRow(r).CreateCell(0).SetCellValue("row" + (r + 1));
        }

        var secret = workbook.CreateSheet("Secret");
        secret.CreateRow(0).CreateCell(0).SetCellValue("hidden-value");
        workbook.SetSheetHidden(1, SheetState.Hidden);

        workbook.CreateSheet("Empty");

        using var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        return stream.ToArray();
    }

    /// <summary>A ZIP (PK signature) whose single entry is ~10 MB of zeros — ratio far above 100.</summary>
    public static byte[] ZipBomb()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml", CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 10; i++)
            {
                entryStream.Write(zeros);
            }
        }

        return stream.ToArray();
    }

    /// <summary>A ZIP with an ordinary-ratio entry of <paramref name="bytes"/> random bytes.</summary>
    public static byte[] PlainZip(int bytes)
    {
        var random = new Random(40);
        var payload = new byte[bytes];
        random.NextBytes(payload);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(payload);
        }

        return stream.ToArray();
    }

    /// <summary>PK signature followed by garbage — not a readable ZIP.</summary>
    public static byte[] CorruptZip() =>
        new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(Enumerable.Repeat((byte)0x11, 64)).ToArray();

    /// <summary>OLE2 signature followed by garbage — NPOI/b2xtranslator cannot parse it.</summary>
    public static byte[] CorruptOle2() =>
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.Concat(Enumerable.Repeat((byte)0x22, 600)).ToArray();

    public static byte[] ForExtension(string extension) => extension switch
    {
        "pdf" => Pdf(),
        "txt" => Encoding.UTF8.GetBytes("plain text body"),
        "doc" => File.ReadAllBytes(LegacyDocPath),
        "docx" => Docx(),
        "xls" => Workbook(isXlsx: false),
        "xlsx" => Workbook(isXlsx: true),
        _ => throw new ArgumentOutOfRangeException(nameof(extension))
    };

    public static FileIndexRecord Record(
        int fileIndexId = 100245,
        string extension = "pdf",
        bool isActive = true,
        string source = "ODrive",
        string? bucketName = "cachet-archive-prod",
        string? relativePath = null) => new()
        {
            FileIndexId = fileIndexId,
            Source = source,
            BucketName = bucketName,
            RelativePath = relativePath ?? $"contracts/2026/file-{fileIndexId}.{extension}",
            FileName = $"file-{fileIndexId}.{extension}",
            FileExtension = extension,
            FileSizeBytes = 1,
            ModifiedAt = new DateTimeOffset(2026, 8, 14, 10, 22, 5, TimeSpan.Zero),
            IsActive = isActive
        };
}

/// <summary>
/// Builds a real <see cref="FileContentService"/> with substituted repository / mirror reader /
/// audit service (the things a unit test cannot control) and a real renderer + limiter by default.
/// </summary>
internal sealed class FileContentServiceHarness
{
    public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
    public IMirrorFileReader MirrorFileReader { get; } = Substitute.For<IMirrorFileReader>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public FilePreviewOptions Options { get; } = new();

    /// <summary>Streams handed out by <see cref="MirrorFileReader"/>, in order — for disposal checks.</summary>
    public List<MemoryStream> OpenedStreams { get; } = new();

    /// <summary>Every <see cref="IAuditLogService.WriteAsync"/> entry received, in call order.</summary>
    public List<AuditLogEntry> AuditEntries =>
        AuditLogService.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditLogService.WriteAsync))
            .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
            .ToList();

    public IOfficePreviewRenderer? RendererOverride { get; set; }

    public FileContentServiceHarness()
    {
        AuditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(1);
    }

    /// <summary>Makes <paramref name="record"/> resolvable and its mirror file hold <paramref name="bytes"/>.</summary>
    public void Seed(FileIndexRecord record, byte[] bytes)
    {
        FileIndexRepository.GetAsync(record.FileIndexId, Arg.Any<CancellationToken>()).Returns(record);
        MirrorFileReader.OpenReadAsync(Arg.Is<FileIndexRecord>(r => r.FileIndexId == record.FileIndexId), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var stream = new MemoryStream(bytes, writable: false);
                OpenedStreams.Add(stream);
                return MirrorFileHandle.Opened(stream);
            });
    }

    public FileContentService Build()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        return new FileContentService(
            FileIndexRepository,
            MirrorFileReader,
            AuditLogService,
            RendererOverride ?? new OfficePreviewRenderer(options),
            new PreviewConversionLimiter(options),
            options,
            NullLogger<FileContentService>.Instance);
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
