using System.Text;
using Cachet.ArchiveSync.Core.Dtos;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using PdfSharp.Pdf;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Files;

/// <summary>
/// CAS-040 §13 fixtures for the HTTP tests — generated at test time (PdfSharp, NPOI, byte arrays);
/// the legacy <c>.doc</c> is the one committed binary, linked from <c>Api.Tests/TestAssets</c>.
/// </summary>
internal static class FileViewerHttpFixtures
{
    public static byte[] Pdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    public static byte[] Docx()
    {
        using var document = new XWPFDocument();
        document.CreateParagraph().CreateRun().SetText("Integration paragraph");
        using var stream = new MemoryStream();
        document.Write(stream);
        return stream.ToArray();
    }

    public static byte[] Workbook(bool isXlsx)
    {
        using IWorkbook workbook = isXlsx ? new XSSFWorkbook() : new HSSFWorkbook();
        workbook.CreateSheet("Sheet1").CreateRow(0).CreateCell(0).SetCellValue("cell-value");
        using var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        return stream.ToArray();
    }

    public static byte[] ForExtension(string extension) => extension switch
    {
        "pdf" => Pdf(),
        "txt" => Encoding.UTF8.GetBytes("plain text — body"),
        "doc" => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "legacy-word-sample.doc")),
        "docx" => Docx(),
        "xls" => Workbook(isXlsx: false),
        "xlsx" => Workbook(isXlsx: true),
        _ => Encoding.UTF8.GetBytes("unsupported")
    };

    public static string DownloadContentType(string extension) => extension switch
    {
        "pdf" => "application/pdf",
        "txt" => "text/plain",
        "doc" => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xls" => "application/vnd.ms-excel",
        _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    public static FileIndexRecord Record(int fileIndexId, string extension, bool isActive = true, string source = "ODrive") => new()
    {
        FileIndexId = fileIndexId,
        Source = source,
        BucketName = FilesApiWebApplicationFactory.Bucket,
        RelativePath = $"contracts/2026/file-{fileIndexId}.{extension}",
        FileName = $"file-{fileIndexId}.{extension}",
        FileExtension = extension,
        FileSizeBytes = 1,
        ModifiedAt = new DateTimeOffset(2026, 8, 14, 10, 22, 5, TimeSpan.Zero),
        IsActive = isActive
    };
}
