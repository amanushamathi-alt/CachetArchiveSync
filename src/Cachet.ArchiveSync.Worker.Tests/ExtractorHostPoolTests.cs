using System.Diagnostics;
using System.Text;
using Cachet.ArchiveSync.Worker.Extraction;
using FluentAssertions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Real child-process tests for out-of-process content extraction: the Worker executable is launched
/// in <c>--extract-host</c> mode, so these prove the crash/timeout containment end to end rather than
/// through mocks.
/// </summary>
public sealed class ExtractorHostPoolTests
{
    private const string CrashExtension = "zzcrash";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private static ProcessStartInfo ChildStartInfo()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Cachet.ArchiveSync.Worker.exe");
        var info = new ProcessStartInfo(exe);
        info.ArgumentList.Add(ExtractorHost.Argument);
        info.Environment[ExtractorHost.TestCrashExtensionVariable] = CrashExtension;
        return info;
    }

    [Fact]
    public async Task Test_ExtractAsync_TxtContainingTerm_ReturnsSnippetFromChild()
    {
        // Arrange
        using var pool = new ExtractorHostPool(ChildStartInfo, size: 1);
        var bytes = Encoding.UTF8.GetBytes("the confidential settlement clause is here");

        // Act
        var result = await pool.ExtractAsync(bytes, "txt", "confidential settlement", Generous, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ExtractorHostOutcome.Match);
        result.Text.Should().Contain("confidential settlement");
    }

    [Fact]
    public async Task Test_ExtractAsync_TermAbsent_ReturnsNoMatch()
    {
        using var pool = new ExtractorHostPool(ChildStartInfo, size: 1);

        var result = await pool.ExtractAsync(Encoding.UTF8.GetBytes("nothing relevant"), "txt", "confidential", Generous, CancellationToken.None);

        result.Outcome.Should().Be(ExtractorHostOutcome.NoMatch);
    }

    [Fact]
    public async Task Test_ExtractAsync_CorruptPdf_ReportsErrorNotCrash_AndChildIsReused()
    {
        using var pool = new ExtractorHostPool(ChildStartInfo, size: 1);

        var bad = await pool.ExtractAsync(Encoding.UTF8.GetBytes("this is not a pdf"), "pdf", "x", Generous, CancellationToken.None);
        var after = await pool.ExtractAsync(Encoding.UTF8.GetBytes("hello world"), "txt", "world", Generous, CancellationToken.None);

        bad.Outcome.Should().Be(ExtractorHostOutcome.Error);
        after.Outcome.Should().Be(ExtractorHostOutcome.Match);
    }

    [Fact]
    public async Task Test_ExtractAsync_ChildCrashesMidRequest_ReportsCrashedAndPoolRecovers()
    {
        // Arrange — size 1 forces the replacement child to serve the follow-up request.
        using var pool = new ExtractorHostPool(ChildStartInfo, size: 1);

        // Act — the child terminates abruptly (FailFast), the same way a stack overflow or native crash would.
        var crashed = await pool.ExtractAsync(new byte[] { 1, 2, 3 }, CrashExtension, "x", Generous, CancellationToken.None);
        var after = await pool.ExtractAsync(Encoding.UTF8.GetBytes("the term is here"), "txt", "term", Generous, CancellationToken.None);

        // Assert — the caller survives, the crash is reported, and the next file is processed normally.
        crashed.Outcome.Should().Be(ExtractorHostOutcome.Crashed);
        after.Outcome.Should().Be(ExtractorHostOutcome.Match);
    }

    [Fact]
    public async Task Test_ExtractAsync_TimeLimitExceeded_KillsChildAndPoolRecovers()
    {
        using var pool = new ExtractorHostPool(ChildStartInfo, size: 1);

        // A fresh child cannot even start and answer within 1 ms, so this deterministically times out.
        var slow = await pool.ExtractAsync(Encoding.UTF8.GetBytes("abc"), "txt", "abc", TimeSpan.FromMilliseconds(1), CancellationToken.None);
        var after = await pool.ExtractAsync(Encoding.UTF8.GetBytes("abc"), "txt", "abc", Generous, CancellationToken.None);

        slow.Outcome.Should().Be(ExtractorHostOutcome.TimedOut);
        after.Outcome.Should().Be(ExtractorHostOutcome.Match);
    }

    [Fact]
    public async Task Test_ExtractAsync_ManyConcurrentRequests_AllCompleteWithBoundedChildren()
    {
        using var pool = new ExtractorHostPool(ChildStartInfo, size: 3);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            pool.ExtractAsync(Encoding.UTF8.GetBytes($"file {i} contains the term"), "txt", "term", Generous, CancellationToken.None)));

        results.Should().OnlyContain(r => r.Outcome == ExtractorHostOutcome.Match);
    }
}
