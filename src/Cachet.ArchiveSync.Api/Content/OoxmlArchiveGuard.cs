using System.IO.Compression;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// CAS-040 §12 parser-DoS control — pre-scans an OOXML (DOCX/XLSX) ZIP central directory with
/// <see cref="System.IO.Compression"/> <b>before</b> NPOI opens the file: rejects a total declared
/// uncompressed size above <c>MaxUncompressedBytes</c> or any entry whose
/// uncompressed/compressed ratio exceeds <c>MaxCompressionRatio</c> (V9 <c>ArchiveGuard</c>).
/// No entry is decompressed.
/// </summary>
public static class OoxmlArchiveGuard
{
    public enum Verdict
    {
        Pass,
        Rejected,

        /// <summary>The bytes are not a readable ZIP archive (V9 <c>ConversionFailed</c>).</summary>
        Unreadable
    }

    public static Verdict Check(byte[] fileBytes, long maxUncompressedBytes, int maxCompressionRatio)
    {
        try
        {
            using var stream = new MemoryStream(fileBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var uncompressed = entry.Length;
                var compressed = entry.CompressedLength;

                total += uncompressed;
                if (total > maxUncompressedBytes)
                {
                    return Verdict.Rejected;
                }

                if (uncompressed > 0)
                {
                    if (compressed <= 0 || (double)uncompressed / compressed > maxCompressionRatio)
                    {
                        return Verdict.Rejected;
                    }
                }
            }

            return Verdict.Pass;
        }
        catch (InvalidDataException)
        {
            return Verdict.Unreadable;
        }
    }
}
