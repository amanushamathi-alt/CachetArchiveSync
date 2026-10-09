using System.Text;
using Cachet.ArchiveSync.Api.Content;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cachet.ArchiveSync.Worker.Extraction;

/// <summary>
/// Child-process side of out-of-process content extraction. The Worker re-launches its own
/// executable with <see cref="Argument"/>; this loop then reads extraction requests from stdin and
/// writes results to stdout until stdin closes (the parent exited or disposed the process).
///
/// Why a separate process: the document parsers (PDF, DOC, XLS, ...) run on untrusted, sometimes
/// corrupt files, and a stack overflow, native crash or out-of-memory in a parser cannot be caught
/// by managed code — it ends the whole process. Running them here means such a failure costs only
/// this child and the one file, never the Worker service.
///
/// Wire format (little-endian). Request: block(extension) block(term) block(file bytes), where
/// block = int32 length + bytes. Response: byte status + block(text). Status 0 = no match,
/// 1 = match (text = snippet), 2 = encrypted file, 3 = extraction error (text = exception text).
/// </summary>
internal static class ExtractorHost
{
    public const string Argument = "--extract-host";

    /// <summary>
    /// Test hook: when set, a request for this extension terminates the process abruptly, so the
    /// crash-containment path can be exercised for real. Inert unless the variable is set on the child.
    /// </summary>
    internal const string TestCrashExtensionVariable = "CACHET_EXTRACT_HOST_TEST_CRASH_EXT";

    internal const byte NoMatch = 0;
    internal const byte Match = 1;
    internal const byte Encrypted = 2;
    internal const byte Error = 3;

    // Deep-recursion parsers overflow the default 1 MB stack on some files; a large stack makes that
    // far less likely, so fewer files are lost to a (contained) crash.
    private const int ExtractionStackBytes = 64 * 1024 * 1024;

    public static int Run(Stream input, Stream output)
    {
        var matcher = new FileContentMatcher(NullLogger<FileContentMatcher>.Instance);
        var crashExtension = Environment.GetEnvironmentVariable(TestCrashExtensionVariable);

        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        while (true)
        {
            string extension;
            string term;
            byte[] data;
            try
            {
                extension = Encoding.UTF8.GetString(ReadBlock(reader));
                term = Encoding.UTF8.GetString(ReadBlock(reader));
                data = ReadBlock(reader);
            }
            catch (EndOfStreamException)
            {
                return 0; // parent closed stdin — normal shutdown
            }

            if (crashExtension is not null && string.Equals(extension, crashExtension, StringComparison.OrdinalIgnoreCase))
            {
                Environment.FailFast("Test-requested extractor crash.");
            }

            var result = default(ContentMatchResult);
            var thread = new Thread(() => result = matcher.Match(data, extension, term), ExtractionStackBytes);
            thread.Start();
            thread.Join();

            byte status;
            string text;
            switch (result.Failure)
            {
                case ContentMatchFailure.Encrypted:
                    status = Encrypted;
                    text = string.Empty;
                    break;
                case ContentMatchFailure.Error:
                    status = Error;
                    text = result.Error is null ? "Unknown extraction error." : FileContentMatcher.Describe(result.Error);
                    break;
                default:
                    status = result.Snippet is null ? NoMatch : Match;
                    text = result.Snippet ?? string.Empty;
                    break;
            }

            writer.Write(status);
            WriteBlock(writer, Encoding.UTF8.GetBytes(text));
            writer.Flush();
        }
    }

    internal static byte[] ReadBlock(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return bytes;
    }

    internal static void WriteBlock(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
