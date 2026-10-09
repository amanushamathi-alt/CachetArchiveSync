using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Unit tests for <see cref="KeywordSearchTaskProcessor"/> (CAS-006 §3/§6/§12; content-match
/// handling redesigned by CAS-012 §2/§7/§10/§13). Every dependency (<see cref="IFileIndexRepository"/>,
/// <see cref="ISearchResultRepository"/>, <see cref="ITaskQueueRepository"/>, <see cref="IB2Client"/>,
/// <see cref="IAuditLogService"/>, <see cref="IMirrorFileReader"/>, <see cref="IFileContentMatcher"/>)
/// is an NSubstitute fake by default — never a real B2 call, never a real DB round trip, never a
/// real filesystem read — matching this processor's <c>IB2Client.ListFileNamesAsync</c>-based
/// cross-check (see the processor's own XML doc DEVIATION note for why it is not
/// <c>GetFileInfoAsync</c>). A handful of CAS-012 tests (SC-1, SC-4, V12) substitute a *real*
/// <see cref="FileContentMatcher"/> in place of the mock specifically to prove the actual
/// read → match → bounded-snippet pipeline end to end, since that is exactly what those criteria
/// assert.
/// </summary>
public sealed class KeywordSearchTaskProcessorTests
{
    private const string ConfiguredBucket = "cachet-archive-prod";

    private sealed class Fixture
    {
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IB2Client B2Client { get; } = Substitute.For<IB2Client>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public IMirrorFileReader MirrorFileReader { get; } = Substitute.For<IMirrorFileReader>();
        public IFileContentMatcher FileContentMatcher { get; } = Substitute.For<IFileContentMatcher>();
        public ILogger<KeywordSearchTaskProcessor> Logger { get; } = Substitute.For<ILogger<KeywordSearchTaskProcessor>>();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["B2:CrossCheckMaxConcurrency"] = "4" }).Build();

        public KeywordSearchTaskProcessor CreateSut(
            IFileContentMatcher? fileContentMatcherOverride = null,
            TaskProgressReporterFactory? progressReporterFactoryOverride = null,
            IConfiguration? configurationOverride = null) =>
            new(FileIndexRepository, SearchResultRepository, TaskQueueRepository, progressReporterFactoryOverride ?? new TaskProgressReporterFactory(TaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), B2Client, AuditLogService,
                MirrorFileReader, fileContentMatcherOverride ?? FileContentMatcher, configurationOverride ?? Configuration, Logger);
    }

    private static ClaimedTaskRow ClaimedTask(int taskQueueId = 100234, int submittedByUserId = 7, string term = "confidential settlement") => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = "KeywordSearch",
        SubmittedByUserId = submittedByUserId,
        SubmittedAt = DateTimeOffset.UtcNow,
        RequestParameters = JsonSerializer.Serialize(new KeywordSearchRequestDto
        {
            Term = term,
            Bucket = ConfiguredBucket,
            Extension = "All"
        })
    };

    private static FileIndexCandidateRow FilenameCandidate(
        int fileIndexId = 1,
        string relativePath = "contracts/2019/agreement-004.pdf",
        string source = "B2",
        string bucketName = ConfiguredBucket) => new()
        {
            FileIndexId = fileIndexId,
            FileName = "agreement-004.pdf",
            RelativePath = relativePath,
            FileSizeBytes = 482113,
            ModifiedAt = DateTimeOffset.UtcNow,
            MatchType = "Filename",
            Source = source,
            BucketName = bucketName
        };

    /// <summary>
    /// CAS-012 §2 step 1: a <c>Content</c> row is an *unfiltered* candidate — <c>MatchSnippet</c> is
    /// always <c>null</c> as returned by <c>SearchCandidatesAsync</c>. The Worker (via
    /// <see cref="IMirrorFileReader"/>/<see cref="IFileContentMatcher"/>) is solely responsible for
    /// producing a non-null snippet, so this fixture never presets one.
    /// </summary>
    private static FileIndexCandidateRow ContentCandidate(
        int fileIndexId = 2,
        string relativePath = "internal/notes/meeting-notes-q3.docx",
        string fileName = "meeting-notes-q3.docx",
        string source = "B2",
        string bucketName = ConfiguredBucket) => new()
        {
            FileIndexId = fileIndexId,
            FileName = fileName,
            RelativePath = relativePath,
            FileSizeBytes = 12345,
            ModifiedAt = DateTimeOffset.UtcNow,
            MatchType = "Content",
            Source = source,
            BucketName = bucketName
        };

    private static void SetConfirmedOnB2(Fixture fixture, string relativePath) =>
        fixture.B2Client.ListFileNamesAsync(relativePath, 1, Arg.Any<CancellationToken>()).Returns(new B2ListFileNamesResult
        {
            Files = new[]
            {
                new B2FileInfo
                {
                    FileId = "b2-file-id-" + relativePath,
                    FileName = relativePath,
                    ContentSha1 = "deadbeef",
                    ContentLength = 100,
                    Action = "upload",
                    UploadTimestamp = 1
                }
            }
        });

    private static void SetNotConfirmedOnB2(Fixture fixture, string relativePath) =>
        fixture.B2Client.ListFileNamesAsync(relativePath, 1, Arg.Any<CancellationToken>())
            .Returns(new B2ListFileNamesResult { Files = Array.Empty<B2FileInfo>() });

    // ---- CAS-035 fixture helpers: the three new Indeterminate-resolving exception shapes -----------

    private static void SetB2ApiExceptionOnCrossCheck(Fixture fixture, string relativePath, int statusCode = 500, string b2Code = "internal_error") =>
        fixture.B2Client.ListFileNamesAsync(relativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(statusCode, b2Code, "some B2-side detail that must never leak"));

    private static void SetHttpRequestExceptionOnCrossCheck(Fixture fixture, string relativePath) =>
        fixture.B2Client.ListFileNamesAsync(relativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("simulated network failure reaching B2"));

    // ---- CAS-012 fixture helpers: IMirrorFileReader / IFileContentMatcher -------------------------

    private static void SetMirrorFileReaderReturnsBytes(Fixture fixture, FileIndexCandidateRow candidate, byte[] bytes) =>
        fixture.MirrorFileReader
            .ReadAllBytesAsync(candidate.Source, candidate.BucketName, candidate.RelativePath, Arg.Any<CancellationToken>())
            .Returns(bytes);

    private static void SetMirrorFileReaderThrows(Fixture fixture, FileIndexCandidateRow candidate, Exception exception) =>
        fixture.MirrorFileReader
            .ReadAllBytesAsync(candidate.Source, candidate.BucketName, candidate.RelativePath, Arg.Any<CancellationToken>())
            .Throws(exception);

    private static void SetContentMatchReturnsSnippet(Fixture fixture, string term, string snippet) =>
        fixture.FileContentMatcher
            .TryMatchAsync(Arg.Any<byte[]>(), Arg.Any<string>(), term, Arg.Any<CancellationToken>())
            .Returns(snippet);

    private static void SetContentMatchReturnsNull(Fixture fixture, string term) =>
        fixture.FileContentMatcher
            .TryMatchAsync(Arg.Any<byte[]>(), Arg.Any<string>(), term, Arg.Any<CancellationToken>())
            .Returns((string?)null);

    /// <summary>
    /// Walks up from the test binary's output directory to locate the repo-root <c>Database/</c>
    /// folder, so SC-1 can assert against the actual deployed <c>dbo.file_index_search.sql</c> text
    /// rather than a hand-copied string (CAS-012 §10 SC-1's SQL-hygiene half).
    /// </summary>
    private static string ReadFileIndexSearchProcedureSql()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidatePath = Path.Combine(directory.FullName, "Database", "CachetArchiveSync", "Stored-Procedures", "dbo.file_index_search.sql");
            if (File.Exists(candidatePath))
            {
                return File.ReadAllText(candidatePath);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate dbo.file_index_search.sql by walking up from the test binary's output directory.");
    }

    private static string StripLeadingBlockComment(string sql)
    {
        var start = sql.IndexOf("/*", StringComparison.Ordinal);
        var end = sql.IndexOf("*/", StringComparison.Ordinal);
        return start != 0 || end < 0 ? sql : sql[(end + 2)..];
    }

    private static List<(LogLevel Level, string Message)> ExtractLoggedMessages(ILogger logger)
    {
        var messages = new List<(LogLevel, string)>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[0] is not LogLevel level || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add((level, message));
            }
        }

        return messages;
    }

    // ---- SC-1: real filesystem content search returns the expected match with a bounded snippet ---

    [Fact]
    public async Task Test_SC1_FilesystemContentSearchReturnsExpectedMatch()
    {
        // Arrange — a real IFileContentMatcher (not mocked) proves the actual read → extract →
        // match → bounded-snippet pipeline, not just that the processor calls through to whatever
        // the mock says.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var candidate = ContentCandidate(fileName: "meeting-notes-q3.txt", relativePath: "internal/notes/meeting-notes-q3.txt");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });

        const string fileText = "Meeting notes: this section discusses the confidential settlement reached in Q3 and the next steps for the team.";
        SetMirrorFileReaderReturnsBytes(fixture, candidate, Encoding.UTF8.GetBytes(fileText));
        SetConfirmedOnB2(fixture, candidate.RelativePath);

        var realContentMatcher = new FileContentMatcher(Substitute.For<ILogger<FileContentMatcher>>());
        var sut = fixture.CreateSut(realContentMatcher);

        string? persistedSnippet = null;
        fixture.SearchResultRepository
            .When(x => x.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => persistedSnippet = callInfo.ArgAt<string?>(5));

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — the file's content match is confirmed and persisted with a bounded snippet
        // (not the full file text) containing the search term.
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, candidate.FileIndexId, "Content",
            Arg.Any<string?>(), "Needs Review", Arg.Any<CancellationToken>());

        persistedSnippet.Should().NotBeNull();
        persistedSnippet!.Should().Contain(term, "the snippet must contain the confirmed match");
        // Bounded means "at most a ±100-char window around the match" — for a file this short,
        // that window legitimately covers the whole text; V12/SC-4 prove the bound against a file
        // long enough for the window to actually truncate something.
        persistedSnippet.Length.Should().BeLessOrEqualTo(2 * 100 + term.Length, "the snippet is at most the ±100-char match window");

        // Assert — the SQL layer's contribution to SC-1: the actual dbo.file_index_search
        // procedure contains no content_text/CONTAINS/FREETEXT reference anywhere (CAS-012 §2
        // step 1 — the SQL Content branch is unfiltered; term matching moved entirely to the
        // Worker, proven above).
        // Only the executable T-SQL body matters here — the mirror file's leading /* ... */ header
        // comment legitimately narrates the column's removal history (same convention CAS-011's own
        // migration header uses) and is expected to mention "content_text" by name for that reason.
        var procedureSql = StripLeadingBlockComment(ReadFileIndexSearchProcedureSql());
        procedureSql.Should().NotContain("content_text", "CAS-012 removed all content_text references from the Content branch's executable SQL");
        procedureSql.Should().NotContain("CONTAINS(", "CAS-012 removed the CONTAINS full-text filter");
        procedureSql.Should().NotContain("FREETEXT(", "CAS-012 never introduced a FREETEXT filter either");
    }

    // ---- SC-2 / V11: a non-matching content candidate is excluded, with no B2 cross-check call ----

    [Fact]
    public async Task Test_SC2_NonMatchingCandidateExcludedWithNoB2Call()
    {
        // Arrange
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var candidate = ContentCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetMirrorFileReaderReturnsBytes(fixture, candidate, Encoding.UTF8.GetBytes("Nothing relevant to the search term is in this file at all."));
        SetContentMatchReturnsNull(fixture, term);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — dropped before any B2 cross-check, no search_result row, no self-heal/audit
        // (that path is reserved for candidates B2 itself rejects, §12/§7 V11).
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().SoftDeleteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());

        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-3 / V10: an unreadable mirror file does not fail the task; only that file is skipped --

    [Fact]
    public async Task Test_SC3_UnreadableFileDoesNotFailTask()
    {
        // Arrange — one candidate's mirror file cannot be read; a second, unrelated candidate is
        // confirmed normally, proving the failure is isolated to the one file.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var unreadableCandidate = ContentCandidate();
        var healthyCandidate = FilenameCandidate(fileIndexId: 3, relativePath: "contracts/2020/agreement-005.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { unreadableCandidate, healthyCandidate });
        SetMirrorFileReaderThrows(fixture, unreadableCandidate, new IOException("simulated disk failure"));
        SetConfirmedOnB2(fixture, healthyCandidate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — never throws; the task still reaches Completed.
        await act.Should().NotThrowAsync();
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());

        // The unreadable candidate produced no B2 call, no search_result row, no self-heal/audit.
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(unreadableCandidate.RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), unreadableCandidate.FileIndexId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The unrelated, healthy candidate still succeeded normally.
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, healthyCandidate.FileIndexId, "Filename", Arg.Is<string?>(s => s == null), "Flagged", Arg.Any<CancellationToken>());

        // The one failed file was logged as a warning, not surfaced as a task failure.
        ExtractLoggedMessages(fixture.Logger).Should().Contain(m => m.Level == LogLevel.Warning);
    }

    // ---- SC-4: no full extracted file text is ever persisted or logged — only the bounded snippet -

    [Fact]
    public async Task Test_SC4_NoFullExtractedContentPersistedOrLogged()
    {
        // Arrange — a real IFileContentMatcher against a long file so a full-text leak (into
        // either the persisted snippet or a log line) would be obviously distinguishable from the
        // bounded ±100-character window.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var candidate = ContentCandidate(fileName: "meeting-notes-q3.txt", relativePath: "internal/notes/meeting-notes-q3.txt");
        var longText =
            string.Concat(Enumerable.Repeat("Lorem ipsum filler text well away from the matched area. ", 40)) +
            "The confidential settlement details follow here in this paragraph of the document. " +
            string.Concat(Enumerable.Repeat("More filler text continues on after the match too. ", 40));
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetMirrorFileReaderReturnsBytes(fixture, candidate, Encoding.UTF8.GetBytes(longText));
        SetConfirmedOnB2(fixture, candidate.RelativePath);

        var realContentMatcher = new FileContentMatcher(Substitute.For<ILogger<FileContentMatcher>>());
        var sut = fixture.CreateSut(realContentMatcher);

        string? persistedSnippet = null;
        fixture.SearchResultRepository
            .When(x => x.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => persistedSnippet = callInfo.ArgAt<string?>(5));

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — persisted snippet is bounded (a strict substring of the full text, far shorter).
        persistedSnippet.Should().NotBeNull();
        longText.Should().Contain(persistedSnippet!);
        persistedSnippet!.Length.Should().BeLessThan(longText.Length / 4);

        // Assert — no log line anywhere contains the full extracted text or the filler far from
        // the match (which would indicate the full text — not just the bounded snippet — reached
        // a log line).
        var messages = ExtractLoggedMessages(fixture.Logger).Select(m => m.Message).ToList();
        messages.Should().OnlyContain(m => !m.Contains(longText, StringComparison.Ordinal));
        messages.Should().OnlyContain(m => !m.Contains("Lorem ipsum filler text well away from the matched area.", StringComparison.Ordinal));
        messages.Should().OnlyContain(m => !m.Contains("More filler text continues on after the match too.", StringComparison.Ordinal));
    }

    // ---- V10 (distinct failure mode from SC-3): an unresolvable mirror mapping is also a skip -----

    [Fact]
    public async Task Test_V10_UnreadableMirrorFileSkippedNotTaskFailure()
    {
        // Arrange — IMirrorFileReader fails because the bucket's mirror mapping cannot be
        // resolved (IBucketMirrorRootRepository's own failure mode, per its XML doc), a distinct
        // cause from SC-3's simulated disk I/O failure but required to be handled identically.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var candidate = ContentCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetMirrorFileReaderThrows(fixture, candidate, new InvalidOperationException("No active odrive_bucket_mapping row found for the requested bucket."));
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — never a task failure; the candidate is silently skipped.
        await act.Should().NotThrowAsync();
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().SoftDeleteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        ExtractLoggedMessages(fixture.Logger).Should().Contain(m => m.Level == LogLevel.Warning);
    }

    // ---- V11 (distinct scenario from SC-2): empty extracted text is treated the same as a miss ----

    [Fact]
    public async Task Test_V11_NonMatchingContentCandidateNeverCrossChecked()
    {
        // Arrange — the file reads successfully but its extracted text is empty (e.g. a
        // scanned/image-only PDF, CAS-012 §14's accepted scope limitation) rather than merely
        // "term not present" — the caller must still treat this identically to any other
        // non-match, never invoking the B2 cross-check.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var candidate = ContentCandidate(fileName: "scanned-only.pdf", relativePath: "internal/scans/scanned-only.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetMirrorFileReaderReturnsBytes(fixture, candidate, Array.Empty<byte>());
        SetContentMatchReturnsNull(fixture, term);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- V12: the Worker-computed content snippet is bounded, never the full extracted text -------

    [Fact]
    public async Task Test_V12_ContentMatchSnippetIsBoundedNotFullText()
    {
        // Arrange — a real IFileContentMatcher against text long enough on both sides of the match
        // that a bounded ±100-character window is clearly distinguishable from the full text.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var candidate = ContentCandidate(fileName: "meeting-notes-q3.txt", relativePath: "internal/notes/meeting-notes-q3.txt");
        var prefix = new string('a', 5000);
        var suffix = new string('b', 5000);
        var fileText = prefix + " the confidential settlement clause is here " + suffix;
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetMirrorFileReaderReturnsBytes(fixture, candidate, Encoding.UTF8.GetBytes(fileText));
        SetConfirmedOnB2(fixture, candidate.RelativePath);

        var realContentMatcher = new FileContentMatcher(Substitute.For<ILogger<FileContentMatcher>>());
        var sut = fixture.CreateSut(realContentMatcher);

        string? persistedSnippet = null;
        fixture.SearchResultRepository
            .When(x => x.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(callInfo => persistedSnippet = callInfo.ArgAt<string?>(5));

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — snippet is far shorter than the 10,000+ character file, and does not include
        // the leading/trailing filler runs.
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, candidate.FileIndexId, "Content",
            Arg.Any<string?>(), "Needs Review", Arg.Any<CancellationToken>());

        persistedSnippet.Should().NotBeNull();
        persistedSnippet!.Length.Should().BeLessThan(300, "the snippet must be the bounded ±100-char window, not the 10,000+ char file");
        persistedSnippet.Should().Contain(term);
        persistedSnippet.Should().NotContain(new string('a', 200));
        persistedSnippet.Should().NotContain(new string('b', 200));
    }

    // ---- SC-3 (CAS-006): local-first candidates, cross-checked against B2, confirmed matches persisted

    [Fact]
    public async Task Test_SC3_WorkerProcessorProducesConfirmedSearchResults()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var filenameCandidate = FilenameCandidate();
        var contentCandidate = ContentCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { filenameCandidate, contentCandidate });
        SetConfirmedOnB2(fixture, filenameCandidate.RelativePath);
        SetConfirmedOnB2(fixture, contentCandidate.RelativePath);
        SetMirrorFileReaderReturnsBytes(fixture, contentCandidate, Encoding.UTF8.GetBytes("...regarding the confidential settlement reached in..."));
        SetContentMatchReturnsSnippet(fixture, "confidential settlement", "...regarding the confidential settlement reached in...");
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — one search_result row per confirmed candidate, correct match_type/status/snippet.
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, filenameCandidate.FileIndexId, "Filename", Arg.Is<string?>(s => s == null), "Flagged", Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, contentCandidate.FileIndexId, "Content", Arg.Is<string?>(s => s == "...regarding the confidential settlement reached in..."), "Needs Review", Arg.Any<CancellationToken>());

        // Never soft-deleted or excluded — both candidates were confirmed.
        await fixture.FileIndexRepository.DidNotReceive().SoftDeleteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckExcluded), Arg.Any<CancellationToken>());

        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-4 (CAS-006) / V9: a candidate the B2 cross-check does not confirm is excluded, self-healed, audited

    [Fact]
    public async Task Test_SC4_StaleCandidateSelfHealsAndExcludes()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var staleCandidate = FilenameCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { staleCandidate });
        SetNotConfirmedOnB2(fixture, staleCandidate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — soft-deleted, audited, and no search_result row was ever created for it.
        await fixture.FileIndexRepository.Received(1).SoftDeleteAsync(staleCandidate.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.CrossCheckExcluded &&
                e.TabModule == "Keyword Search" &&
                e.FilePath == staleCandidate.RelativePath &&
                e.TaskQueueId == task.TaskQueueId &&
                e.UserId == task.SubmittedByUserId),
            Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- V9 (explicit, B2NotFoundException path): excluded candidate never persisted ---------------

    [Fact]
    public async Task Test_V9_CrossCheckExcludedCandidateNeverPersisted()
    {
        // Arrange — the cross-check throws B2NotFoundException rather than returning an empty list
        // (the other exclusion path IsConfirmedOnB2Async handles).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var candidate = FilenameCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        fixture.B2Client.ListFileNamesAsync(candidate.RelativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new B2NotFoundException("file_not_present"));
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.Received(1).SoftDeleteAsync(candidate.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-5: task reaches Completed with zero matches --------------------------------------------

    [Fact]
    public async Task Test_SC5_TaskCompletesEvenWithZeroMatches()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow>());
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId,
            Arg.Any<string>(),
            "Completed",
            Arg.Is<string?>(detail => IsZeroCountCompletionSummary(detail)),
            Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: unhandled failure marks the task Failed, never throws out of ProcessAsync

    [Fact]
    public async Task Test_ProcessAsync_UnhandledException_MarksTaskFailed()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("simulated database failure"));
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — never propagates; marks the task Failed instead (Worker's claim loop must keep running).
        await act.Should().NotThrowAsync();
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-11 (CAS-006): no B2 key, session token, or raw file/term content ever appears in a log line

    [Fact]
    public async Task Test_SC11_NoCredentialOrContentLeakageInLogs()
    {
        // Arrange
        const string sensitiveTerm = "acme-corp-merger-confidential-code-7734";
        var fixture = new Fixture();
        var task = new ClaimedTaskRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            SubmittedByUserId = 7,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new KeywordSearchRequestDto
            {
                Term = sensitiveTerm,
                Bucket = ConfiguredBucket,
                Extension = "All"
            })
        };
        var candidate = ContentCandidate();
        // §11: content-match snippets must never reach a log line either — produced here via the
        // IFileContentMatcher mock (CAS-012: SQL no longer supplies MatchSnippet for Content rows).
        const string sensitiveSnippet = "SECRET-CONTENT-SNIPPET-MUST-NOT-BE-LOGGED";
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", sensitiveTerm, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetMirrorFileReaderReturnsBytes(fixture, candidate, Encoding.UTF8.GetBytes("irrelevant bytes — matcher is mocked below"));
        SetContentMatchReturnsSnippet(fixture, sensitiveTerm, sensitiveSnippet);
        SetConfirmedOnB2(fixture, candidate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        var messages = new List<string>();
        foreach (var call in fixture.Logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(m =>
            !m.Contains(sensitiveTerm, StringComparison.Ordinal) &&
            !m.Contains(sensitiveSnippet, StringComparison.Ordinal));
    }

    // ==== CAS-035 — B2 cross-check indeterminate failure isolation (§2, §9 V1-V5, §10 SC-1..SC-7) ====

    // ---- SC-1 / V1: one Indeterminate candidate among many still completes the whole task -----------

    [Fact]
    public async Task Test_SC1_OneIndeterminateCandidateAmongManyStillCompletesTask()
    {
        // Arrange — 4 candidates; exactly one raises a non-404 B2ApiException (simulating CAS-034's
        // retries already exhausted), the other 3 confirm normally with no exception at all.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var confirmed1 = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2019/agreement-001.pdf");
        var confirmed2 = FilenameCandidate(fileIndexId: 2, relativePath: "contracts/2019/agreement-002.pdf");
        var confirmed3 = FilenameCandidate(fileIndexId: 3, relativePath: "contracts/2019/agreement-003.pdf");
        var indeterminate = FilenameCandidate(fileIndexId: 4, relativePath: "contracts/2019/agreement-004.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { confirmed1, confirmed2, confirmed3, indeterminate });
        SetConfirmedOnB2(fixture, confirmed1.RelativePath);
        SetConfirmedOnB2(fixture, confirmed2.RelativePath);
        SetConfirmedOnB2(fixture, confirmed3.RelativePath);
        SetB2ApiExceptionOnCrossCheck(fixture, indeterminate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — task reaches Completed (never Failed); N-1 = 3 matches recorded; IndeterminateCount = 1.
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());

        var summary = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        (summary.FilenameMatchCount + summary.ContentMatchCount).Should().Be(3);
        summary.IndeterminateCount.Should().Be(1);
    }

    // ---- SC-2 / V2: the Indeterminate candidate is never persisted, never soft-deleted, audited once -

    [Fact]
    public async Task Test_SC2_IndeterminateCandidateNotPersistedNotSoftDeleted()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var indeterminate = FilenameCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { indeterminate });
        SetB2ApiExceptionOnCrossCheck(fixture, indeterminate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — no search_result row, no self-heal soft-delete, exactly one CrossCheckIndeterminate
        // audit row for this candidate.
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), indeterminate.FileIndexId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().SoftDeleteAsync(indeterminate.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.CrossCheckIndeterminate &&
                e.FilePath == indeterminate.RelativePath &&
                e.TaskQueueId == task.TaskQueueId &&
                e.UserId == task.SubmittedByUserId &&
                e.TabModule == "Keyword Search"),
            Arg.Any<CancellationToken>());
    }

    // ---- SC-3: an HttpRequestException resolves Indeterminate identically to a non-404 B2ApiException

    [Fact]
    public async Task Test_SC3_HttpRequestExceptionResolvesIndeterminate()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var candidate = FilenameCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetHttpRequestExceptionOnCrossCheck(fixture, candidate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — same disposition as a plain B2ApiException: Completed, IndeterminateCount = 1,
        // one CrossCheckIndeterminate audit row, no insert, no soft-delete.
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        var summary = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        summary.IndeterminateCount.Should().Be(1);
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckIndeterminate && e.FilePath == candidate.RelativePath),
            Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), candidate.FileIndexId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().SoftDeleteAsync(candidate.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-4 / V4: an internal-timeout OperationCanceledException resolves Indeterminate; the same --
    // ---- exception type with ct.IsCancellationRequested == true is NOT caught and propagates unchanged

    [Fact]
    public async Task Test_SC4_InternalTimeoutIndeterminate_CallerCancellationPropagates()
    {
        // Part A — internal timeout: ct.IsCancellationRequested == false when B2Client raises
        // OperationCanceledException (CAS-034 §2's own retry-timeout, not the caller's ct firing) —
        // resolves the candidate Indeterminate and the task still Completes.
        var fixtureA = new Fixture();
        var taskA = ClaimedTask();
        var candidateA = FilenameCandidate();
        fixtureA.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidateA });
        fixtureA.B2Client.ListFileNamesAsync(candidateA.RelativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException("simulated internal B2Client retry-timeout"));
        var sutA = fixtureA.CreateSut();

        await sutA.ProcessAsync(taskA, CancellationToken.None);

        await fixtureA.TaskQueueRepository.Received(1).UpdateStatusAsync(
            taskA.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        var summaryA = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(CompletedResultDetail(fixtureA.TaskQueueRepository)!)!;
        summaryA.IndeterminateCount.Should().Be(1);
        await fixtureA.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckIndeterminate), Arg.Any<CancellationToken>());

        // Part B — caller-requested cancellation: ct.IsCancellationRequested == true at the exact
        // moment the same exception type is raised must NOT be caught by the new §2.1 guard — it
        // propagates out of ProcessAsync unchanged, exactly as before this spec.
        var fixtureB = new Fixture();
        var taskB = ClaimedTask();
        var candidateB = FilenameCandidate();
        var cts = new CancellationTokenSource();
        fixtureB.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidateB });
        fixtureB.B2Client.ListFileNamesAsync(candidateB.RelativePath, 1, Arg.Any<CancellationToken>())
            .Returns<B2ListFileNamesResult>(_ =>
            {
                // Cancel the exact same token that flows into ProcessAsync as `ct`, right as the B2
                // call raises — matching §2.1's `when (!ct.IsCancellationRequested)` guard being
                // false at the moment of the catch.
                cts.Cancel();
                throw new OperationCanceledException("caller cancellation reached mid B2 call", cts.Token);
            });
        var sutB = fixtureB.CreateSut();

        // Act
        var act = async () => await sutB.ProcessAsync(taskB, cts.Token);

        // Assert — propagates unchanged; never resolved Indeterminate, never marked Completed/Failed.
        await act.Should().ThrowAsync<OperationCanceledException>();
        await fixtureB.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixtureB.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- V1: an indeterminate B2 failure must not fail the whole task --------------------------------

    [Fact]
    public async Task Test_V1_IndeterminateB2FailureDoesNotFailWholeTask()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var confirmed = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2019/agreement-001.pdf");
        var indeterminate = FilenameCandidate(fileIndexId: 2, relativePath: "contracts/2019/agreement-002.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { confirmed, indeterminate });
        SetConfirmedOnB2(fixture, confirmed.RelativePath);
        SetB2ApiExceptionOnCrossCheck(fixture, indeterminate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — never throws, never marked Failed; still reaches Completed with the confirmed
        // candidate's match durably recorded.
        await act.Should().NotThrowAsync();
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, confirmed.FileIndexId, "Filename", Arg.Any<string?>(), "Flagged", Arg.Any<CancellationToken>());
    }

    // ---- V2: Indeterminate is audited distinctly from CrossCheckExcluded, never soft-deleted ---------

    [Fact]
    public async Task Test_V2_IndeterminateOutcomeAuditedDistinctlyFromExcluded()
    {
        // Arrange — one candidate B2 itself confirms gone (ConfirmedAbsent/CrossCheckExcluded), one
        // whose cross-check call errors (Indeterminate/CrossCheckIndeterminate) — the two outcomes,
        // and their audit rows, must never be cross-labeled.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var excluded = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2011/stale.pdf");
        var indeterminate = FilenameCandidate(fileIndexId: 2, relativePath: "contracts/2019/agreement-002.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { excluded, indeterminate });
        SetNotConfirmedOnB2(fixture, excluded.RelativePath);
        SetB2ApiExceptionOnCrossCheck(fixture, indeterminate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.FileIndexRepository.Received(1).SoftDeleteAsync(excluded.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().SoftDeleteAsync(indeterminate.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckExcluded && e.FilePath == excluded.RelativePath),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckIndeterminate && e.FilePath == indeterminate.RelativePath),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.FilePath == excluded.RelativePath && e.Action == AuditAction.CrossCheckIndeterminate),
            Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.FilePath == indeterminate.RelativePath && e.Action == AuditAction.CrossCheckExcluded),
            Arg.Any<CancellationToken>());
    }

    // ---- V3: IndeterminateCount is populated on a Completed task's summary ---------------------------

    [Fact]
    public async Task Test_V3_IndeterminateCountPopulatedOnCompletedSummary()
    {
        // Arrange — one confirmed match and two independently-Indeterminate candidates (one per
        // exception type), so the counter must reflect a total, not just "at least one."
        var fixture = new Fixture();
        var task = ClaimedTask();
        var confirmed = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2019/agreement-001.pdf");
        var indeterminate1 = FilenameCandidate(fileIndexId: 2, relativePath: "contracts/2019/agreement-002.pdf");
        var indeterminate2 = FilenameCandidate(fileIndexId: 3, relativePath: "contracts/2019/agreement-003.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { confirmed, indeterminate1, indeterminate2 });
        SetConfirmedOnB2(fixture, confirmed.RelativePath);
        SetB2ApiExceptionOnCrossCheck(fixture, indeterminate1.RelativePath);
        SetHttpRequestExceptionOnCrossCheck(fixture, indeterminate2.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        var summary = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        summary.IndeterminateCount.Should().Be(2);
        summary.FilenameMatchCount.Should().Be(1);
        summary.FailureReason.Should().BeNull();
    }

    // ---- V4: caller-requested cancellation still propagates unchanged, NOT caught as Indeterminate ---

    [Fact]
    public async Task Test_V4_CallerCancellationStillPropagatesNotIndeterminate()
    {
        // Arrange — ct.IsCancellationRequested == true at the moment the B2 call raises
        // OperationCanceledException; the new §2.1 guard must not swallow it.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var candidate = FilenameCandidate();
        var cts = new CancellationTokenSource();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        fixture.B2Client.ListFileNamesAsync(candidate.RelativePath, 1, Arg.Any<CancellationToken>())
            .Returns<B2ListFileNamesResult>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException("caller cancellation reached mid B2 call", cts.Token);
            });
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, cts.Token);

        // Assert — propagates out of ProcessAsync; never audited as CrossCheckIndeterminate, never
        // transitioned to Completed or Failed.
        await act.Should().ThrowAsync<OperationCanceledException>();
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckIndeterminate), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- V5 (regression): a non-B2 exception during disposition still fails the whole task -----------

    [Fact]
    public async Task Test_V5_NonB2DispositionFailureStillFailsWholeTask()
    {
        // Arrange — the candidate's B2 cross-check itself succeeds (Confirmed), but the disposition
        // write (search_result insert) throws a generic, non-B2 exception. The new §2.1 catch clauses
        // wrap only the B2 cross-check call itself, never the disposition branch (§2.2) — this must
        // still fail the whole task exactly as before this spec, proving the new isolation logic does
        // not accidentally swallow a non-B2 failure.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var candidate = FilenameCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "confidential settlement", Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetConfirmedOnB2(fixture, candidate.RelativePath);
        fixture.SearchResultRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("simulated search_result insert failure"));
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — never throws out of ProcessAsync (the Worker's claim loop must keep running), but
        // the task is marked Failed, never Completed, and never audited as CrossCheckIndeterminate.
        await act.Should().NotThrowAsync();
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckIndeterminate), Arg.Any<CancellationToken>());
    }

    // ---- SC-7: no B2 credential/token/search-term/file-content ever appears in an Indeterminate log --

    [Fact]
    public async Task Test_SC7_NoCredentialOrSensitiveDataInIndeterminateLogLines()
    {
        // Arrange — a sensitive search term and a fake B2-secret-shaped string embedded in each
        // exception's own Message, plus one candidate per new Indeterminate-resolving exception type
        // (B2ApiException, HttpRequestException, internal-timeout OperationCanceledException). Only
        // file_index_id and, for B2ApiException, the B2 status code, may appear in the new catch
        // clauses' log lines (§2.1/§10 SC-7) — never the term, the exception's own Message, or any
        // credential-shaped value.
        const string sensitiveTerm = "acme-corp-merger-confidential-code-7734";
        const string fakeB2Secret = "b2-application-key-0012deadbeef01234567890abcdef-must-never-leak";
        var fixture = new Fixture();
        var task = ClaimedTask(term: sensitiveTerm);
        var b2ApiCandidate = FilenameCandidate(fileIndexId: 11, relativePath: "contracts/2019/agreement-011.pdf");
        var httpCandidate = FilenameCandidate(fileIndexId: 12, relativePath: "contracts/2019/agreement-012.pdf");
        var timeoutCandidate = FilenameCandidate(fileIndexId: 13, relativePath: "contracts/2019/agreement-013.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", sensitiveTerm, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { b2ApiCandidate, httpCandidate, timeoutCandidate });
        fixture.B2Client.ListFileNamesAsync(b2ApiCandidate.RelativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(503, "service_unavailable", fakeB2Secret));
        fixture.B2Client.ListFileNamesAsync(httpCandidate.RelativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException(fakeB2Secret));
        fixture.B2Client.ListFileNamesAsync(timeoutCandidate.RelativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException(fakeB2Secret));
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — no log line anywhere leaks the term or the fake secret.
        var messages = ExtractLoggedMessages(fixture.Logger).Select(m => m.Message).ToList();
        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(m =>
            !m.Contains(sensitiveTerm, StringComparison.Ordinal) &&
            !m.Contains(fakeB2Secret, StringComparison.Ordinal));

        // The three new Indeterminate log lines each carry file_index_id and, for the B2ApiException
        // case, the B2 status code.
        messages.Should().Contain(m => m.Contains($"file_index_id {b2ApiCandidate.FileIndexId}", StringComparison.Ordinal) && m.Contains("status 503", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains($"file_index_id {httpCandidate.FileIndexId}", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains($"file_index_id {timeoutCandidate.FileIndexId}", StringComparison.Ordinal));
    }

    // ==== CAS-029 — progress tracking (§3.1 steps 3/4e/5, §3.6) ======================================

    /// <summary>
    /// CAS-029: a clock on which every timestamp read is one hour after the previous one, so the
    /// 15 s interval has always elapsed and every <c>ItemCompletedAsync</c> writes — making each
    /// per-candidate increment observable. (<c>FakeTimeProvider.AutoAdvanceAmount</c> does not
    /// apply to <c>GetTimestamp</c>, which is what the reporter reads.)
    /// </summary>
    private sealed class AlwaysElapsedTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerHour);
    }

    private static TaskProgressReporterFactory EveryItemWritesProgressFactory(ITaskQueueRepository repository) =>
        new(repository, new AlwaysElapsedTimeProvider(),
            Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>());

    private static List<(int Processed, int Total)> ProgressWrites(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateProgressAsync))
            .Select(c => ((int)c.GetArguments()[2]!, (int)c.GetArguments()[3]!))
            .ToList();

    private static string? CompletedResultDetail(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                && (string)c.GetArguments()[2]! == "Completed")
            .Select(c => (string?)c.GetArguments()[3])
            .Single();

    private static B2ListFileNamesResult ConfirmedListing(string relativePath) => new()
    {
        Files = new[]
        {
            new B2FileInfo
            {
                FileId = "b2-file-id-" + relativePath,
                FileName = relativePath,
                ContentSha1 = "deadbeef",
                ContentLength = 100,
                Action = "upload",
                UploadTimestamp = 1
            }
        }
    };

    // ---- CAS-029 SC-3: first progress write is (0, candidates.Count), before any mirror read / B2 call

    [Fact]
    public async Task Test_SC3_KeywordSearch_StartWrite_ZeroOfCandidateCount_BeforeFirstB2Call()
    {
        // Arrange — one Content candidate (mirror read + B2) and one Filename candidate (B2 only).
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var content = ContentCandidate();
        var filename = FilenameCandidate(fileIndexId: 3, relativePath: "contracts/2020/agreement-005.pdf");
        var events = new ConcurrentQueue<string>();

        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                events.Enqueue("search");
                return (IReadOnlyList<FileIndexCandidateRow>)new List<FileIndexCandidateRow> { content, filename };
            });
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                events.Enqueue($"progress:{call.ArgAt<int>(2)}/{call.ArgAt<int>(3)}");
                return true;
            });
        fixture.MirrorFileReader
            .ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                events.Enqueue("mirror");
                return Encoding.UTF8.GetBytes("text");
            });
        SetContentMatchReturnsSnippet(fixture, term, "...confidential settlement...");
        fixture.B2Client.ListFileNamesAsync(Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                events.Enqueue("b2");
                return ConfirmedListing(call.ArgAt<string>(0));
            });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — search, then the (0, 2) start write, then all mirror/B2 work.
        var ordered = events.ToList();
        ordered.Take(2).Should().Equal("search", "progress:0/2");
        ordered.Skip(2).Should().Contain("mirror").And.Contain("b2");
        ordered.IndexOf("progress:0/2").Should().BeLessThan(ordered.IndexOf("mirror"));
        ordered.IndexOf("progress:0/2").Should().BeLessThan(ordered.IndexOf("b2"));
        await fixture.TaskQueueRepository.Received(1).UpdateProgressAsync(
            task.TaskQueueId, "worker-svc", 0, 2, Arg.Any<CancellationToken>());
    }

    // ---- CAS-029 SC-4: all four dispositions each counted exactly once; final write (N, N) ----------

    [Fact]
    public async Task Test_SC4_KeywordSearch_EveryDispositionCountedOnce_FinalEqualsTotal()
    {
        // Arrange — filename-confirmed, content-confirmed, content-dropped, B2-excluded.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var filenameConfirmed = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2019/agreement-004.pdf");
        var contentConfirmed = ContentCandidate(fileIndexId: 2, relativePath: "notes/confirmed.txt", fileName: "confirmed.txt");
        var contentDropped = ContentCandidate(fileIndexId: 3, relativePath: "notes/dropped.txt", fileName: "dropped.txt");
        var excluded = FilenameCandidate(fileIndexId: 4, relativePath: "contracts/2011/stale.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { filenameConfirmed, contentConfirmed, contentDropped, excluded });

        SetMirrorFileReaderReturnsBytes(fixture, contentConfirmed, Encoding.UTF8.GetBytes("match"));
        SetMirrorFileReaderReturnsBytes(fixture, contentDropped, Encoding.UTF8.GetBytes("no-match"));
        fixture.FileContentMatcher.TryMatchAsync(Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "match"), Arg.Any<string>(), term, Arg.Any<CancellationToken>())
            .Returns("...confidential settlement...");
        fixture.FileContentMatcher.TryMatchAsync(Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "no-match"), Arg.Any<string>(), term, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        SetConfirmedOnB2(fixture, filenameConfirmed.RelativePath);
        SetConfirmedOnB2(fixture, contentConfirmed.RelativePath);
        SetNotConfirmedOnB2(fixture, excluded.RelativePath);
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        // Sequential: the exact per-item progress-write sequence below is only deterministic one file at a time.
        var sequential = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["B2:CrossCheckMaxConcurrency"] = "1", ["Worker:ContentMatchMaxConcurrency"] = "1" }).Build();
        var sut = fixture.CreateSut(progressReporterFactoryOverride: EveryItemWritesProgressFactory(fixture.TaskQueueRepository), configurationOverride: sequential);

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — the four dispositions really happened.
        await fixture.SearchResultRepository.Received(2).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.Received(1).SoftDeleteAsync(excluded.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(contentDropped.RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Assert — every candidate advanced the count by exactly one (each item wrote), final (4, 4).
        ProgressWrites(fixture.TaskQueueRepository).Should().Equal((0, 4), (1, 4), (2, 4), (3, 4), (4, 4), (4, 4));
    }

    // ---- CAS-029 SC-5 (KeywordSearch): results/audits → final progress → Completed ------------------

    [Fact]
    public async Task Test_SC5_KeywordSearch_ResultsThenProgressThenCompleted()
    {
        // Arrange — filename-confirmed, content-confirmed, B2-excluded. Concurrency 1 makes the
        // per-candidate order deterministic so Received.InOrder can assert the full sequence.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var filenameConfirmed = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2019/agreement-004.pdf");
        var contentConfirmed = ContentCandidate(fileIndexId: 2);
        var excluded = FilenameCandidate(fileIndexId: 4, relativePath: "contracts/2011/stale.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { filenameConfirmed, contentConfirmed, excluded });
        SetMirrorFileReaderReturnsBytes(fixture, contentConfirmed, Encoding.UTF8.GetBytes("match"));
        SetContentMatchReturnsSnippet(fixture, term, "...confidential settlement...");
        SetConfirmedOnB2(fixture, filenameConfirmed.RelativePath);
        SetConfirmedOnB2(fixture, contentConfirmed.RelativePath);
        SetNotConfirmedOnB2(fixture, excluded.RelativePath);
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sequential = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["B2:CrossCheckMaxConcurrency"] = "1", ["Worker:ContentMatchMaxConcurrency"] = "1" }).Build();
        var sut = fixture.CreateSut(configurationOverride: sequential);

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — ordering (§3.6).
        Received.InOrder(() =>
        {
            fixture.SearchResultRepository.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, filenameConfirmed.FileIndexId,
                "Filename", Arg.Any<string?>(), "Flagged", Arg.Any<CancellationToken>());
            fixture.SearchResultRepository.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, contentConfirmed.FileIndexId,
                "Content", Arg.Any<string?>(), "Needs Review", Arg.Any<CancellationToken>());
            fixture.FileIndexRepository.SoftDeleteAsync(excluded.FileIndexId, Arg.Any<string>(), Arg.Any<CancellationToken>());
            fixture.AuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckExcluded), Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateProgressAsync(task.TaskQueueId, Arg.Any<string>(), 3, 3, Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });

        // Assert — summary counts equal the result-row writes.
        var resultRowWrites = fixture.SearchResultRepository.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ISearchResultRepository.InsertAsync));
        var summary = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        (summary.FilenameMatchCount + summary.ContentMatchCount).Should().Be(resultRowWrites).And.Be(2);
        summary.FilenameMatchCount.Should().Be(1);
        summary.ContentMatchCount.Should().Be(1);
        summary.ExcludedCount.Should().Be(1);
    }

    // ---- CAS-029 SC-10: a throwing progress write never changes the task's outcome -----------------

    [Fact]
    public async Task Test_SC10_KeywordSearch_ProgressWriteThrows_TaskStillCompletes()
    {
        // Arrange — every progress write throws.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        const string exceptionText = "progress-write-exception-text-must-not-leak";
        var task = ClaimedTask(term: term);
        var confirmed = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2019/agreement-004.pdf");
        var excluded = FilenameCandidate(fileIndexId: 4, relativePath: "contracts/2011/stale.pdf");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { confirmed, excluded });
        SetConfirmedOnB2(fixture, confirmed.RelativePath);
        SetNotConfirmedOnB2(fixture, excluded.RelativePath);
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException(exceptionText));
        var sut = fixture.CreateSut(progressReporterFactoryOverride: EveryItemWritesProgressFactory(fixture.TaskQueueRepository));

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — progress was attempted (and failed), yet the task Completed with the right summary.
        ProgressWrites(fixture.TaskQueueRepository).Should().NotBeEmpty();
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        var resultDetail = CompletedResultDetail(fixture.TaskQueueRepository);
        resultDetail.Should().NotBeNull().And.NotContain(exceptionText).And.NotContain(nameof(InvalidOperationException));
        var summary = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(resultDetail!)!;
        summary.FilenameMatchCount.Should().Be(1);
        summary.ContentMatchCount.Should().Be(0);
        summary.ExcludedCount.Should().Be(1);
        summary.FailureReason.Should().BeNull();
    }

    // ==== CAS-038 — optional created_at range pass-through (§2.6, §10 SC-1/SC-2 Worker half) ==========

    // ---- CAS-038 SC-1 (Worker half): keyword-only payload -> SearchCandidatesAsync(..., null, null) --

    [Fact]
    public async Task Test_SC1_KeywordOnlyRequestPersistsNullDatesAndSearchesUnfiltered()
    {
        // Arrange — the existing keyword-only payload shape (no dates set).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var candidate = FilenameCandidate();
        fixture.FileIndexRepository
            .SearchCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { candidate });
        SetConfirmedOnB2(fixture, candidate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — candidate query called exactly once, with no date range.
        await fixture.FileIndexRepository.Received(1).SearchCandidatesAsync(
            ConfiguredBucket, "All", "confidential settlement", (DateOnly?)null, (DateOnly?)null, Arg.Any<CancellationToken>());

        // Keyword-only behavior unchanged: the candidate is cross-checked, persisted, task Completed.
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, candidate.FileIndexId, "Filename", Arg.Is<string?>(s => s == null), "Flagged", Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 SC-2 (Worker half): persisted dates are passed through to SearchCandidatesAsync ----

    [Fact]
    public async Task Test_SC2_DateRangeRequestPersistsAndIsPassedToCandidateQuery()
    {
        // Arrange — a payload exactly as KeywordSearchService persists it for a date-range request.
        var fromDate = new DateOnly(2025, 1, 10);
        var toDate = new DateOnly(2025, 3, 31);
        var fixture = new Fixture();
        var task = ClaimedTask() with
        {
            RequestParameters = JsonSerializer.Serialize(new KeywordSearchRequestDto
            {
                Term = "confidential settlement",
                Bucket = ConfiguredBucket,
                Extension = "PDF",
                FromDate = fromDate,
                ToDate = toDate
            })
        };
        fixture.FileIndexRepository
            .SearchCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow>());
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — the same dates reach the candidate query, alongside the unchanged bucket/extension/term.
        await fixture.FileIndexRepository.Received(1).SearchCandidatesAsync(
            ConfiguredBucket, "PDF", "confidential settlement", fromDate, toDate, Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ==== CAS-038 Amendment A2 — date-only search (§2 A2 point 3, §10 SC-10) ===========================

    [Fact]
    public async Task Test_A2_SC10_DateOnlyTaskStoresInformationalAndSkipsContentMatching()
    {
        // Arrange — a payload exactly as KeywordSearchService persists a date-only request (Term = "").
        // The keyword-task half of SC-10 ("still Flagged / Needs Review") is covered by the existing
        // Test_SC3_WorkerProcessorProducesConfirmedSearchResults and Test_SC1_KeywordOnlyRequestPersistsNullDatesAndSearchesUnfiltered.
        var fromDate = new DateOnly(2025, 1, 1);
        var toDate = new DateOnly(2025, 1, 31);
        var fixture = new Fixture();
        var task = ClaimedTask() with
        {
            RequestParameters = JsonSerializer.Serialize(new KeywordSearchRequestDto
            {
                Term = "",
                Bucket = ConfiguredBucket,
                Extension = "All",
                FromDate = fromDate,
                ToDate = toDate
            })
        };
        var filename = FilenameCandidate(fileIndexId: 1, relativePath: "contracts/2025/agreement-001.pdf");
        var content = ContentCandidate(fileIndexId: 2, relativePath: "internal/notes/meeting-notes-q1.docx");
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", "", fromDate, toDate, Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { filename, content });
        SetConfirmedOnB2(fixture, filename.RelativePath);
        SetConfirmedOnB2(fixture, content.RelativePath);
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sequentialConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["B2:CrossCheckMaxConcurrency"] = "1", ["Worker:ContentMatchMaxConcurrency"] = "1" }).Build();
        var sut = fixture.CreateSut(progressReporterFactoryOverride: EveryItemWritesProgressFactory(fixture.TaskQueueRepository), configurationOverride: sequentialConfig);

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — the Filename candidate is stored as 'Informational' (not 'Flagged'), and it is the only insert.
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, filename.FileIndexId, "Filename",
            Arg.Is<string?>(s => s == null), "Informational", Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Assert — no content matching at all, and no B2 cross-check for the dropped Content candidate.
        fixture.MirrorFileReader.ReceivedCalls().Should().BeEmpty();
        fixture.FileContentMatcher.ReceivedCalls().Should().BeEmpty();
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(content.RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.B2Client.Received(1).ListFileNamesAsync(filename.RelativePath, 1, Arg.Any<CancellationToken>());

        // Assert — each candidate counted exactly once (start (0,2), two items, final (2,2)).
        ProgressWrites(fixture.TaskQueueRepository).Should().Equal((0, 2), (1, 2), (2, 2), (2, 2));

        // Assert — task Completed with one filename match and no content matches.
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        var summary = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        summary.FilenameMatchCount.Should().Be(1);
        summary.ContentMatchCount.Should().Be(0);
    }

    // Extracted so the SC-5 Arg.Is<string?> predicate (an expression tree) never itself contains a
    // call with optional arguments (JsonSerializer.Deserialize<T> has one) — CS0854.
    private static bool IsZeroCountCompletionSummary(string? detail)
    {
        if (detail is null)
        {
            return false;
        }

        var summary = JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(detail);
        return summary is not null
            && summary.FilenameMatchCount == 0
            && summary.ContentMatchCount == 0
            && summary.ExcludedCount == 0;
    }

    // ==== CAS-047 — search modes: Filename / Keyword / Date (§2, §9 V5/V6, §10 SC-4/5/6/8/9) ====

    private static ClaimedTaskRow ModeTask(
        string? mode, string term, DateOnly? fromDate = null, DateOnly? toDate = null, int taskQueueId = 100234) => new()
        {
            TaskQueueId = taskQueueId,
            TaskType = "KeywordSearch",
            SubmittedByUserId = 7,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new KeywordSearchRequestDto
            {
                Term = term,
                Bucket = ConfiguredBucket,
                Extension = "All",
                FromDate = fromDate,
                ToDate = toDate,
                Mode = mode
            })
        };

    private static void StubCandidates(Fixture fixture, string term, DateOnly? from, DateOnly? to, string? source, params FileIndexCandidateRow[] rows) =>
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, from, to, Arg.Any<CancellationToken>(), source)
            .Returns(rows.ToList());

    [Fact]
    public async Task Test_SC4_FilenameModeNoB2NoMirrorSetBasedInsert()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ModeTask("Filename", "  agreement  ");
        fixture.SearchResultRepository
            .InsertFilenameMatchesAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(42);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — one set-based insert with the trimmed term and the worker identity.
        await fixture.SearchResultRepository.Received(1).InsertFilenameMatchesAsync(
            task.TaskQueueId, ConfiguredBucket, "All", "agreement", "worker-svc", Arg.Any<CancellationToken>());

        // No B2 call, no mirror read, no candidate query, no per-row insert.
        fixture.B2Client.ReceivedCalls().Should().BeEmpty();
        fixture.MirrorFileReader.ReceivedCalls().Should().BeEmpty();
        fixture.FileIndexRepository.ReceivedCalls().Should().BeEmpty();
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Task completes with the inserted count.
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, "worker-svc", "Completed",
            Arg.Is<string?>(d => d != null && d.Contains("\"FilenameMatchCount\":42")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC4_FilenameModeNoB2NoMirrorSetBasedInsert_RepositoryFailureMarksTaskFailed()
    {
        // Arrange — failure branch of the Filename path.
        var fixture = new Fixture();
        var task = ModeTask("Filename", "agreement");
        fixture.SearchResultRepository
            .InsertFilenameMatchesAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("sql down"));
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, "worker-svc", "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC5_KeywordContentOnlyDateUnchanged()
    {
        // Arrange — Keyword: SQL returns a Filename-type row and a Content row; only content may be stored.
        const string term = "confidential";
        var fixture = new Fixture();
        var filenameRow = FilenameCandidate(fileIndexId: 1);
        var contentRow = ContentCandidate(fileIndexId: 2);
        StubCandidates(fixture, term, null, null, "B2", filenameRow, contentRow);
        SetMirrorFileReaderReturnsBytes(fixture, contentRow, Encoding.UTF8.GetBytes("x"));
        SetContentMatchReturnsSnippet(fixture, term, "confidential snippet");
        SetConfirmedOnB2(fixture, contentRow.RelativePath);
        SetConfirmedOnB2(fixture, filenameRow.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(ModeTask("Keyword", term), CancellationToken.None);

        // Assert — B2-only scope requested; only the Content row stored; no Filename-type row.
        await fixture.FileIndexRepository.Received(1).SearchCandidatesAsync(
            ConfiguredBucket, "All", term, null, null, Arg.Any<CancellationToken>(), "B2");
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), 100234, 2, "Content", Arg.Any<string?>(), "Needs Review", Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), "Filename", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertFilenameMatchesAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // The dropped Filename-type row is never cross-checked.
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(filenameRow.RelativePath, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC5_KeywordContentOnlyDateUnchanged_DateModeStoresInformationalB2Scoped()
    {
        // Arrange — Date: blank term, range, B2 scope, Informational status, cross-check still runs.
        var from = new DateOnly(2025, 1, 10);
        var to = new DateOnly(2025, 1, 12);
        var fixture = new Fixture();
        var row = FilenameCandidate(fileIndexId: 5);
        StubCandidates(fixture, string.Empty, from, to, "B2", row);
        SetConfirmedOnB2(fixture, row.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(ModeTask("Date", string.Empty, from, to), CancellationToken.None);

        // Assert
        await fixture.FileIndexRepository.Received(1).SearchCandidatesAsync(
            ConfiguredBucket, "All", string.Empty, from, to, Arg.Any<CancellationToken>(), "B2");
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), 100234, 5, "Filename", Arg.Is<string?>(x => x == null), "Informational", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC6_LegacyTaskUnchanged()
    {
        // Arrange — no mode: all sources (source null), both branches, today's statuses.
        const string term = "settlement";
        var fixture = new Fixture();
        var filenameRow = FilenameCandidate(fileIndexId: 1, source: "ODrive");
        var contentRow = ContentCandidate(fileIndexId: 2);
        StubCandidates(fixture, term, null, null, null, filenameRow, contentRow);
        SetMirrorFileReaderReturnsBytes(fixture, contentRow, Encoding.UTF8.GetBytes("x"));
        SetContentMatchReturnsSnippet(fixture, term, "settlement snippet");
        SetConfirmedOnB2(fixture, filenameRow.RelativePath);
        SetConfirmedOnB2(fixture, contentRow.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(ModeTask(null, term), CancellationToken.None);

        // Assert
        await fixture.FileIndexRepository.Received(1).SearchCandidatesAsync(
            ConfiguredBucket, "All", term, null, null, Arg.Any<CancellationToken>(), null);
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), 100234, 1, "Filename", Arg.Is<string?>(x => x == null), "Flagged", Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), 100234, 2, "Content", Arg.Any<string?>(), "Needs Review", Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertFilenameMatchesAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Test_SC6_LegacyTaskUnchanged_PayloadWithoutModeKeyDeserializesToNullMode()
    {
        // Arrange — a pre-CAS-047 stored payload has no "mode" key at all.
        var json = "{\"Term\":\"x\",\"Bucket\":\"b\",\"Extension\":\"All\"}";

        // Act
        var dto = JsonSerializer.Deserialize<KeywordSearchRequestDto>(json);

        // Assert
        dto!.Mode.Should().BeNull();
    }

    [Fact]
    public async Task Test_SC8_KeywordDateCrossCheckUnchanged()
    {
        // Arrange — Keyword mode, three content candidates: confirmed, absent on B2, indeterminate.
        const string term = "settlement";
        var fixture = new Fixture();
        var confirmed = ContentCandidate(fileIndexId: 1, relativePath: "a/confirmed.docx", fileName: "confirmed.docx");
        var absent = ContentCandidate(fileIndexId: 2, relativePath: "a/absent.docx", fileName: "absent.docx");
        var indeterminate = ContentCandidate(fileIndexId: 3, relativePath: "a/indeterminate.docx", fileName: "indeterminate.docx");
        StubCandidates(fixture, term, null, null, "B2", confirmed, absent, indeterminate);
        foreach (var c in new[] { confirmed, absent, indeterminate })
        {
            SetMirrorFileReaderReturnsBytes(fixture, c, Encoding.UTF8.GetBytes("x"));
        }

        SetContentMatchReturnsSnippet(fixture, term, "settlement snippet");
        SetConfirmedOnB2(fixture, confirmed.RelativePath);
        fixture.B2Client.ListFileNamesAsync(absent.RelativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new B2NotFoundException("gone"));
        SetHttpRequestExceptionOnCrossCheck(fixture, indeterminate.RelativePath);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(ModeTask("Keyword", term), CancellationToken.None);

        // Assert — confirmed stored; absent soft-deleted + CrossCheckExcluded; indeterminate audited, never stored/healed.
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), 100234, 1, "Content", Arg.Any<string?>(), "Needs Review", Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.Received(1).SoftDeleteAsync(2, "worker-svc", Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().SoftDeleteAsync(3, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckExcluded && e.FilePath == absent.RelativePath), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckIndeterminate && e.FilePath == indeterminate.RelativePath), Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC8_KeywordDateCrossCheckUnchanged_DateModeExcludesAbsentRow()
    {
        // Arrange — Date mode: one row absent on B2 is excluded and self-healed, not stored.
        var from = new DateOnly(2025, 1, 10);
        var to = new DateOnly(2025, 1, 12);
        var fixture = new Fixture();
        var row = FilenameCandidate(fileIndexId: 9);
        StubCandidates(fixture, string.Empty, from, to, "B2", row);
        fixture.B2Client.ListFileNamesAsync(row.RelativePath, 1, Arg.Any<CancellationToken>())
            .Throws(new B2NotFoundException("gone"));
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(ModeTask("Date", string.Empty, from, to), CancellationToken.None);

        // Assert
        await fixture.FileIndexRepository.Received(1).SoftDeleteAsync(9, "worker-svc", Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Filename")]
    [InlineData("Keyword")]
    public async Task Test_SC9_TermNeverLogged(string mode)
    {
        // Arrange
        const string sensitiveTerm = "acme-merger-code-7734";
        var fixture = new Fixture();
        var content = ContentCandidate();
        StubCandidates(fixture, sensitiveTerm, null, null, "B2", content);
        SetMirrorFileReaderReturnsBytes(fixture, content, Encoding.UTF8.GetBytes("x"));
        SetContentMatchReturnsSnippet(fixture, sensitiveTerm, "snippet");
        SetConfirmedOnB2(fixture, content.RelativePath);
        fixture.SearchResultRepository
            .InsertFilenameMatchesAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(3);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(ModeTask(mode, sensitiveTerm), CancellationToken.None);

        // Assert — no log line, and no audit entry, carries the term.
        var messages = ExtractLoggedMessages(fixture.Logger);
        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(m => !m.Message.Contains(sensitiveTerm, StringComparison.Ordinal));
        foreach (var call in fixture.AuditLogService.ReceivedCalls())
        {
            call.GetArguments().Select(a => a?.ToString() ?? string.Empty)
                .Should().OnlyContain(a => !a.Contains(sensitiveTerm, StringComparison.Ordinal));
        }
    }

    // ---- Identical files (same SHA-1 + size) are read and matched once per search --------------------

    [Fact]
    public async Task Test_KeywordSearch_IdenticalContentCandidates_MatchedOnceAndBothReported()
    {
        // Arrange
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var first = ContentCandidate(fileIndexId: 1, relativePath: "a/copy1.txt", fileName: "copy1.txt") with { Sha1Hash = "same-hash" };
        var second = ContentCandidate(fileIndexId: 2, relativePath: "b/copy2.txt", fileName: "copy2.txt") with { Sha1Hash = "same-hash" };
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { first, second });
        SetMirrorFileReaderReturnsBytes(fixture, first, Encoding.UTF8.GetBytes("x"));
        SetMirrorFileReaderReturnsBytes(fixture, second, Encoding.UTF8.GetBytes("x"));
        SetContentMatchReturnsSnippet(fixture, term, "...confidential settlement...");
        SetConfirmedOnB2(fixture, first.RelativePath);
        SetConfirmedOnB2(fixture, second.RelativePath);
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — one extraction, two results.
        await fixture.FileContentMatcher.Received(1)
            .TryMatchAsync(Arg.Any<byte[]>(), Arg.Any<string>(), term, Arg.Any<CancellationToken>());
        await fixture.SearchResultRepository.Received(2).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, Arg.Any<int>(), "Content", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_KeywordSearch_UnreadableCopy_DoesNotHideReadableIdenticalCopy()
    {
        // Arrange — same hash; the first copy cannot be read, the second can.
        var fixture = new Fixture();
        const string term = "confidential settlement";
        var task = ClaimedTask(term: term);
        var unreadable = ContentCandidate(fileIndexId: 1, relativePath: "a/missing.txt", fileName: "missing.txt") with { Sha1Hash = "same-hash" };
        var readable = ContentCandidate(fileIndexId: 2, relativePath: "b/present.txt", fileName: "present.txt") with { Sha1Hash = "same-hash" };
        fixture.FileIndexRepository
            .SearchCandidatesAsync(ConfiguredBucket, "All", term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { unreadable, readable });
        SetMirrorFileReaderThrows(fixture, unreadable, new IOException("missing"));
        SetMirrorFileReaderReturnsBytes(fixture, readable, Encoding.UTF8.GetBytes("x"));
        SetContentMatchReturnsSnippet(fixture, term, "...confidential settlement...");
        SetConfirmedOnB2(fixture, readable.RelativePath);
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.SearchResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, readable.FileIndexId, "Content", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ==== CAS-054 - resume from watermark (§2.4/§2.5, V4, SC-4, SC-5, SC-9) ==========================

    // ---- V4: skips exactly the candidates at or below W; everything above W is re-checked -----------

    [Fact]
    public async Task Test_V4_ResumedTaskSkipsOnlyCandidatesAtOrBelowWatermark()
    {
        // Arrange - 6 candidates, W = 3; every candidate matches.
        var harness = new ResumeTestHarness();
        harness.SetCandidates(6);
        harness.MatchingFileIndexIds.UnionWith(Enumerable.Range(1, 6));
        var sut = harness.CreateSut();

        // Act
        await sut.ProcessAsync(ResumeTestHarness.Task(200, resumeWatermark: 3, restartedFrom: 100), CancellationToken.None);

        // Assert - only 4, 5, 6 were checked and inserted; 3 (== W) is skipped, 4 (W + 1) is not.
        harness.StoredFileIndexIds(200).Should().Equal(4, 5, 6);
        foreach (var skipped in new[] { 1, 2, 3 })
        {
            await harness.B2Client.DidNotReceive().ListFileNamesAsync(
                ResumeTestHarness.PathFor(skipped), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        harness.FinalStatus().Should().Be("Completed");
    }

    // ---- SC-4: zero mirror reads / extractions for candidates at or below W --------------------------

    [Fact]
    public async Task Test_SC4_ResumedTaskDoesNotReadCandidatesAtOrBelowWatermark()
    {
        // Arrange
        var harness = new ResumeTestHarness();
        harness.SetCandidates(5);
        harness.MatchingFileIndexIds.UnionWith(new[] { 4, 5 });
        var sut = harness.CreateSut();

        // Act
        await sut.ProcessAsync(ResumeTestHarness.Task(200, resumeWatermark: 3, restartedFrom: 100), CancellationToken.None);

        // Assert - no mirror read for 1..3; every candidate above W is read exactly once.
        foreach (var skipped in new[] { 1, 2, 3 })
        {
            await harness.MirrorFileReader.DidNotReceive().ReadAllBytesAsync(
                Arg.Any<string>(), Arg.Any<string>(), ResumeTestHarness.PathFor(skipped), Arg.Any<CancellationToken>());
        }

        foreach (var processed in new[] { 4, 5 })
        {
            await harness.MirrorFileReader.Received(1).ReadAllBytesAsync(
                Arg.Any<string>(), Arg.Any<string>(), ResumeTestHarness.PathFor(processed), Arg.Any<CancellationToken>());
        }

        await harness.FileContentMatcher.Received(2).TryMatchAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-5: progress continues from the skipped count and ends at items_total ---------------------

    [Fact]
    public async Task Test_SC5_ResumedProgressStartsAtSkippedCountAndReachesTotal()
    {
        // Arrange - 10 candidates, W = 6 -> 6 skipped, 4 processed. total stays 10.
        var harness = new ResumeTestHarness();
        harness.SetCandidates(10);
        var sut = harness.CreateSut();

        // Act
        await sut.ProcessAsync(ResumeTestHarness.Task(200, resumeWatermark: 6, restartedFrom: 100), CancellationToken.None);

        // Assert
        var writes = harness.ProgressWrites();
        writes.Should().OnlyContain(w => w.Total == 10, "items_total is the FULL candidate count");
        writes.First().Should().Be((0, 10), "the start write precedes the skipped-count catch-up");
        writes.Should().Contain((6, 10), "items_processed passes through the skipped count");
        writes.Select(w => w.Processed).Should().BeInAscendingOrder();
        writes.Last().Should().Be((10, 10), "the final write has items_processed = items_total");
        harness.FinalStatus().Should().Be("Completed");
    }

    // ---- SC-9: summary names the source task and counts include carried-over rows --------------------

    [Fact]
    public async Task Test_SC9_ResumedSummaryNamesSourceTaskAndIncludesCarriedCounts()
    {
        // Arrange - W = 3; carried-over rows (content 1 and 3, filename 2) already live under the
        // new task, and one new content match (5) is found above W. Candidate 4 does not match.
        var harness = new ResumeTestHarness();
        harness.SetCandidates(5);
        harness.MatchingFileIndexIds.Add(5);
        harness.Store.Add((200, 1, "Content"));
        harness.Store.Add((200, 3, "Content"));
        harness.Store.Add((200, 2, "Filename"));
        var sut = harness.CreateSut();

        // Act
        await sut.ProcessAsync(ResumeTestHarness.Task(200, resumeWatermark: 3, restartedFrom: 100), CancellationToken.None);

        // Assert
        var summary = harness.CompletedSummary();
        summary.ResumedFromTaskQueueId.Should().Be(100);
        summary.ContentMatchCount.Should().Be(3, "2 carried over + 1 new");
        summary.FilenameMatchCount.Should().Be(1, "the carried-over filename row is counted");
        summary.ExcludedCount.Should().Be(0);
        summary.IndeterminateCount.Should().Be(0);
    }
}
