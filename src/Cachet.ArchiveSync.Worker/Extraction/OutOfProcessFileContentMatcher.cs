using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker.Extraction;

/// <summary>
/// <see cref="IFileContentMatcher"/> for the Worker: runs document extraction in a child process
/// (<see cref="ExtractorHostPool"/>) so a parser that crashes, hangs or runs out of memory on one file
/// cannot take the service down. A crashed or timed-out file is logged (the caller's logging scope
/// supplies its name and path) and treated as a skip — exactly like any other unreadable file — and the
/// search carries on with the remaining files.
///
/// Large plain-text files are scanned as a stream in this process: that is simple managed code with no
/// document parser behind it, and it avoids copying hundreds of megabytes through a pipe.
/// Never logs extracted text, file content or the search term.
/// </summary>
internal sealed class OutOfProcessFileContentMatcher : IFileContentMatcher
{
    private readonly ExtractorHostPool _pool;
    private readonly FileContentMatcher _streamMatcher;
    private readonly ILogger<OutOfProcessFileContentMatcher> _logger;
    private readonly TimeSpan _timeout;

    public OutOfProcessFileContentMatcher(
        ExtractorHostPool pool,
        ILogger<FileContentMatcher> streamMatcherLogger,
        ILogger<OutOfProcessFileContentMatcher> logger,
        IConfiguration configuration)
    {
        _pool = pool;
        _streamMatcher = new FileContentMatcher(streamMatcherLogger);
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int?>("Worker:ContentMatchTimeoutSeconds") ?? 120));
    }

    public Task<string?> TryMatchStreamAsync(Stream content, string fileExtension, string term, CancellationToken ct) =>
        _streamMatcher.TryMatchStreamAsync(content, fileExtension, term, ct);

    public async Task<string?> TryMatchAsync(byte[] fileBytes, string fileExtension, string term, CancellationToken ct)
    {
        if (!FileContentMatcher.IsSupportedExtension(fileExtension))
        {
            return null;
        }

        var result = await _pool.ExtractAsync(fileBytes, fileExtension, term, _timeout, ct);

        switch (result.Outcome)
        {
            case ExtractorHostOutcome.Match:
                return result.Text;

            case ExtractorHostOutcome.Encrypted:
                _logger.LogWarning(
                    "Content extraction skipped: encrypted or password-protected file with extension {FileExtension}.",
                    fileExtension);
                return null;

            case ExtractorHostOutcome.Error:
                _logger.LogWarning(
                    "Content extraction failed for a file with extension {FileExtension}; skipping content match for this candidate. {ExtractionError}",
                    fileExtension, Truncate(result.Text));
                return null;

            case ExtractorHostOutcome.Crashed:
                _logger.LogWarning(
                    "Content extraction CRASHED the extractor process (exit code {ExitCode}) for a file with extension {FileExtension}; the file is skipped and the search continues. {ProcessOutput}",
                    result.ExitCode, fileExtension, Truncate(result.Text));
                return null;

            case ExtractorHostOutcome.TimedOut:
                _logger.LogWarning(
                    "Content extraction exceeded the {TimeoutSeconds}s time limit for a file with extension {FileExtension}; the extractor process was killed and the file is skipped.",
                    (int)_timeout.TotalSeconds, fileExtension);
                return null;

            default:
                return null;
        }
    }

    // Keep each log entry small: a full parser stack trace per skipped file bloats the log (and the
    // Event Log, if enabled) for no extra diagnostic value.
    private static string? Truncate(string? text) =>
        text is { Length: > 1500 } ? text[..1500] + "...[truncated]" : text;
}
