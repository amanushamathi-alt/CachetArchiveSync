using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-040 §13 — <see cref="Services.FileContentService"/>: SC-1 (metadata view modes), V3–V6/V8
/// (pipeline outcomes and their Failure audit rows), V10 (audit failure serves no content) and
/// SC-9 (audit-before-response ordering). Repository, mirror reader and audit service are
/// substituted; the renderer and conversion limiter are real.
/// </summary>
public sealed class FileContentServiceTests
{
    private const int UserId = 7;

    private readonly FileContentServiceHarness _harness = new();

    public FileContentServiceTests()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private Task<FileAccessResult> InvokeAsync(string operation, int fileIndexId, string context = "KeywordSearch")
    {
        var sut = _harness.Build();
        return operation switch
        {
            "view" => sut.GetViewAsync(fileIndexId, context, UserId, CancellationToken.None),
            "download" => sut.GetDownloadAsync(fileIndexId, context, UserId, CancellationToken.None),
            _ => sut.GetMetadataAsync(fileIndexId, context, CancellationToken.None)
        };
    }

    // -----------------------------------------------------------------------------------------
    // SC-1
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf", FileViewModes.InlinePdf)]
    [InlineData("txt", FileViewModes.InlineText)]
    [InlineData("doc", FileViewModes.ExtractedText)]
    [InlineData("docx", FileViewModes.HtmlPreview)]
    [InlineData("xls", FileViewModes.HtmlPreview)]
    [InlineData("xlsx", FileViewModes.HtmlPreview)]
    public async Task Test_SC1_MetadataReturnsViewModePerFileType(string extension, string expectedViewMode)
    {
        // Under cap, correct signature.
        var record = FileViewerTestFixtures.Record(fileIndexId: 100, extension: extension);
        var bytes = FileViewerTestFixtures.ForExtension(extension);
        _harness.Seed(record, bytes);

        var result = await InvokeAsync("metadata", 100);

        result.Outcome.Should().Be(FileContentOutcome.Success);
        var metadata = result.Metadata!;
        metadata.ViewMode.Should().Be(expectedViewMode);
        metadata.DownloadOnlyReason.Should().BeNull();
        metadata.PreviewTruncated.Should().BeFalse();
        metadata.FileIndexId.Should().Be(100);
        metadata.Name.Should().Be(record.FileName);
        metadata.Extension.Should().Be(extension);
        metadata.SizeBytes.Should().Be(bytes.Length);
        metadata.Source.Should().Be("ODrive");
        metadata.ModifiedAt.Should().Be(record.ModifiedAt);

        // The response contains no path, relative or absolute.
        var json = JsonSerializer.Serialize(metadata);
        json.Should().NotContain("contracts", "the relative path is never returned (§6.1)");
        json.Should().NotContainEquivalentOf("path");

        // Metadata is not audited (§6.1).
        _harness.AuditEntries.Should().BeEmpty();

        // Over cap -> DownloadOnly/TooLarge (txt is never DownloadOnly: it is truncated instead).
        _harness.Options.PdfMaxBytes = 4;
        _harness.Options.OfficeMaxBytes = 4;
        _harness.Options.TextMaxBytes = 4;
        var overCap = (await InvokeAsync("metadata", 100)).Metadata!;
        if (extension == "txt")
        {
            overCap.ViewMode.Should().Be(FileViewModes.InlineText);
            overCap.PreviewTruncated.Should().BeTrue("a TXT over TextMaxBytes is truncated, not refused (§8.1)");
        }
        else
        {
            overCap.ViewMode.Should().Be(FileViewModes.DownloadOnly);
            overCap.DownloadOnlyReason.Should().Be("TooLarge");
        }

        // Signature mismatch -> DownloadOnly/SignatureMismatch (txt has no signature check).
        _harness.Options.PdfMaxBytes = 52_428_800;
        _harness.Options.OfficeMaxBytes = 20_971_520;
        _harness.Options.TextMaxBytes = 2_097_152;
        var mismatchRecord = FileViewerTestFixtures.Record(fileIndexId: 101, extension: extension);
        _harness.Seed(mismatchRecord, FileViewerTestFixtures.RenamedHtmlPdf());
        var mismatch = (await InvokeAsync("metadata", 101)).Metadata!;
        if (extension == "txt")
        {
            mismatch.ViewMode.Should().Be(FileViewModes.InlineText);
        }
        else
        {
            mismatch.ViewMode.Should().Be(FileViewModes.DownloadOnly);
            mismatch.DownloadOnlyReason.Should().Be("SignatureMismatch");
        }
    }

    // -----------------------------------------------------------------------------------------
    // V3
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view", AuditAction.FileViewed)]
    [InlineData("download", AuditAction.FileDownloaded)]
    [InlineData("metadata", null)]
    public async Task Test_V3_UnknownFileIndexIdReturns404AndAuditsFailure(string operation, AuditAction? expectedAction)
    {
        _harness.FileIndexRepository.GetAsync(999, Arg.Any<CancellationToken>()).Returns((FileIndexRecord?)null);

        var result = await InvokeAsync(operation, 999);

        result.Outcome.Should().Be(FileContentOutcome.FileNotFound);
        result.Content.Should().BeNull();
        await _harness.MirrorFileReader.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default);

        if (expectedAction is null)
        {
            _harness.AuditEntries.Should().BeEmpty("the metadata endpoint is not audited (§6.1)");
            return;
        }

        var entry = _harness.AuditEntries.Should().ContainSingle().Subject;
        entry.Action.Should().Be(expectedAction.Value);
        entry.Result.Should().Be(AuditResult.Failure);
        entry.FilePath.Should().BeNull("V3 has no row, so no relative path (§7)");
        entry.UserId.Should().Be(UserId);
        entry.TabModule.Should().Be("Keyword Search");
        entry.ResultDetail.Should().Be("fileIndexId=999; reason=FileIndexNotFound");
    }

    // -----------------------------------------------------------------------------------------
    // V4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view", AuditAction.FileViewed)]
    [InlineData("download", AuditAction.FileDownloaded)]
    public async Task Test_V4_InactiveRowReturns410AndAuditsFailure(string operation, AuditAction expectedAction)
    {
        var record = FileViewerTestFixtures.Record(fileIndexId: 200, extension: "pdf", isActive: false);
        _harness.Seed(record, FileViewerTestFixtures.Pdf());

        var result = await InvokeAsync(operation, 200, context: "ODriveSync");

        result.Outcome.Should().Be(FileContentOutcome.NoLongerAvailable);
        result.Content.Should().BeNull();
        await _harness.MirrorFileReader.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default);
        var entry = _harness.AuditEntries.Should().ContainSingle().Subject;
        entry.Action.Should().Be(expectedAction);
        entry.Result.Should().Be(AuditResult.Failure);
        entry.TabModule.Should().Be("O-Drive Sync");
        entry.FilePath.Should().Be(record.RelativePath);
        entry.ResultDetail.Should().Be("fileIndexId=200; reason=NoLongerAvailable");
    }

    // -----------------------------------------------------------------------------------------
    // V5
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view", "png")]
    [InlineData("download", "exe")]
    [InlineData("view", null)]
    [InlineData("metadata", "html")]
    public async Task Test_V5_OutOfScopeExtensionReturns415(string operation, string? extension)
    {
        var record = FileViewerTestFixtures.Record(fileIndexId: 300) with { FileExtension = extension };
        _harness.Seed(record, new byte[] { 1, 2, 3 });

        var result = await InvokeAsync(operation, 300, context: "Quarantine");

        result.Outcome.Should().Be(FileContentOutcome.UnsupportedType);
        await _harness.MirrorFileReader.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default);
        if (operation == "metadata")
        {
            _harness.AuditEntries.Should().BeEmpty();
            return;
        }

        var entry = _harness.AuditEntries.Should().ContainSingle().Subject;
        entry.Result.Should().Be(AuditResult.Failure);
        entry.TabModule.Should().Be("Quarantine");
        entry.ResultDetail.Should().Be("fileIndexId=300; reason=UnsupportedType");
    }

    // -----------------------------------------------------------------------------------------
    // V6
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view")]
    [InlineData("download")]
    [InlineData("metadata")]
    public async Task Test_V6_MissingBucketMappingReturns500WithoutPath(string operation)
    {
        var record = FileViewerTestFixtures.Record(fileIndexId: 400, extension: "pdf");
        _harness.FileIndexRepository.GetAsync(400, Arg.Any<CancellationToken>()).Returns(record);
        _harness.MirrorFileReader.OpenReadAsync(Arg.Any<FileIndexRecord>(), Arg.Any<CancellationToken>())
            .Returns(MirrorFileHandle.Failed(MirrorFileOpenOutcome.MappingMissing));

        var result = await InvokeAsync(operation, 400);

        result.Outcome.Should().Be(FileContentOutcome.MappingMissing);
        result.Content.Should().BeNull();
        JsonSerializer.Serialize(result with { Content = null }).Should().NotContain("contracts", "the outcome carries no path");
        if (operation == "metadata")
        {
            _harness.AuditEntries.Should().BeEmpty();
            return;
        }

        var entry = _harness.AuditEntries.Should().ContainSingle().Subject;
        entry.Result.Should().Be(AuditResult.Failure);
        entry.ResultDetail.Should().Be("fileIndexId=400; reason=MappingMissing");
        entry.ResultDetail.Should().NotContain(":\\").And.NotContain("contracts");
    }

    // -----------------------------------------------------------------------------------------
    // V8 (and the V7 service-level mapping — the containment check itself is proven in
    // MirrorFileReaderOpenReadTests)
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view", MirrorFileOpenOutcome.MissingFromMirror, FileContentOutcome.MissingFromMirror, "MissingFromMirror")]
    [InlineData("download", MirrorFileOpenOutcome.MissingFromMirror, FileContentOutcome.MissingFromMirror, "MissingFromMirror")]
    [InlineData("metadata", MirrorFileOpenOutcome.MissingFromMirror, FileContentOutcome.MissingFromMirror, null)]
    [InlineData("view", MirrorFileOpenOutcome.PathRejected, FileContentOutcome.PathRejected, "PathRejected")]
    public async Task Test_V8_FileAbsentFromMirrorReturns404AndAuditsFailure(
        string operation, MirrorFileOpenOutcome readerOutcome, FileContentOutcome expected, string? expectedReason)
    {
        var record = FileViewerTestFixtures.Record(fileIndexId: 500, extension: "docx");
        _harness.FileIndexRepository.GetAsync(500, Arg.Any<CancellationToken>()).Returns(record);
        _harness.MirrorFileReader.OpenReadAsync(Arg.Any<FileIndexRecord>(), Arg.Any<CancellationToken>())
            .Returns(MirrorFileHandle.Failed(readerOutcome));

        var result = await InvokeAsync(operation, 500);

        result.Outcome.Should().Be(expected);
        result.Content.Should().BeNull();
        if (expectedReason is null)
        {
            _harness.AuditEntries.Should().BeEmpty();
            return;
        }

        var entry = _harness.AuditEntries.Should().ContainSingle().Subject;
        entry.Result.Should().Be(AuditResult.Failure);
        entry.FilePath.Should().Be(record.RelativePath);
        entry.ResultDetail.Should().Be($"fileIndexId=500; reason={expectedReason}");
    }

    // -----------------------------------------------------------------------------------------
    // V10
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view", "pdf")]
    [InlineData("view", "txt")]
    [InlineData("view", "docx")]
    [InlineData("download", "xlsx")]
    [InlineData("view-failure", "pdf")]
    public async Task Test_V10_AuditWriteFailureServesNoContent(string operation, string extension)
    {
        var record = FileViewerTestFixtures.Record(fileIndexId: 600, extension: extension, isActive: operation != "view-failure");
        _harness.Seed(record, FileViewerTestFixtures.ForExtension(extension));
        _harness.AuditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit store down"));

        var result = await InvokeAsync(operation == "view-failure" ? "view" : operation, 600);

        result.Outcome.Should().Be(FileContentOutcome.AuditWriteFailed);
        result.Content.Should().BeNull("no content bytes are returned when the audit row could not be written");
        result.ContentType.Should().BeNull();
        await _harness.AuditLogService.ReceivedWithAnyArgs(1).WriteAsync(default!, default);

        // The file handle is disposed.
        foreach (var stream in _harness.OpenedStreams)
        {
            stream.CanRead.Should().BeFalse("the mirror handle must be disposed when nothing is served");
        }
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view", "KeywordSearch", "Keyword Search", AuditAction.FileViewed, "pdf")]
    [InlineData("view", "ODriveSync", "O-Drive Sync", AuditAction.FileViewed, "txt")]
    [InlineData("view", "Quarantine", "Quarantine", AuditAction.FileViewed, "xlsx")]
    [InlineData("download", "KeywordSearch", "Keyword Search", AuditAction.FileDownloaded, "docx")]
    [InlineData("download", "ODriveSync", "O-Drive Sync", AuditAction.FileDownloaded, "doc")]
    [InlineData("download", "Quarantine", "Quarantine", AuditAction.FileDownloaded, "xls")]
    public async Task Test_SC9_ViewAndDownloadAuditBeforeResponse(
        string operation, string context, string expectedTabModule, AuditAction expectedAction, string extension)
    {
        var record = FileViewerTestFixtures.Record(fileIndexId: 700, extension: extension, source: "B2");
        _harness.Seed(record, FileViewerTestFixtures.ForExtension(extension));

        // The audit write is held open: the service must not produce its result until it completes.
        var auditGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _harness.AuditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(_ => auditGate.Task);

        var pending = InvokeAsync(operation, 700, context);

        // Wait until the audit call has been made, then prove no result exists yet.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (_harness.AuditEntries.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        _harness.AuditEntries.Should().ContainSingle("the audit write is issued before any result");
        await Task.Delay(50);
        pending.IsCompleted.Should().BeFalse("no result (and so no response byte) may exist before the audit row is written (V10)");

        auditGate.SetResult(1);
        var result = await pending;

        result.Outcome.Should().Be(FileContentOutcome.Success);
        result.Content.Should().NotBeNull();

        Received.InOrder(() =>
        {
            _harness.FileIndexRepository.GetAsync(700, Arg.Any<CancellationToken>());
            _harness.MirrorFileReader.OpenReadAsync(Arg.Any<FileIndexRecord>(), Arg.Any<CancellationToken>());
            _harness.AuditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        });

        var entry = _harness.AuditEntries.Should().ContainSingle("exactly one audit row per successful view/download").Subject;
        entry.UserId.Should().Be(UserId);
        entry.TabModule.Should().Be(expectedTabModule);
        entry.Action.Should().Be(expectedAction);
        entry.FilePath.Should().Be(record.RelativePath);
        Path.IsPathRooted(entry.FilePath!).Should().BeFalse("file_path is relative_path, never absolute");
        entry.Result.Should().Be(AuditResult.Success);
        entry.CreatedBy.Should().Be("api-svc");
        entry.UpdatedBy.Should().Be("api-svc");
        entry.TaskQueueId.Should().BeNull();
        entry.ResultDetail.Should().StartWith("fileIndexId=700; source=B2; ");

        await result.Content!.DisposeAsync();
    }
}
