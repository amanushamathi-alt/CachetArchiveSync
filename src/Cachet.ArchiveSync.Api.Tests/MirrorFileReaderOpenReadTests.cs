using System.Diagnostics;
using Cachet.ArchiveSync.Api.Storage;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-040 §13 — <see cref="MirrorFileReader.OpenReadAsync"/> (§6 pipeline steps 3–6): SC-11
/// (root resolved by <c>source</c>; traversal and absolute paths rejected) and V7 (containment
/// check: <c>..</c>, absolute path, sibling-prefix root, reparse point). Runs against a real
/// per-test temp directory, deleted on dispose; <see cref="IBucketMirrorRootRepository"/> is
/// substituted.
/// </summary>
public sealed class MirrorFileReaderOpenReadTests : IDisposable
{
    private const string Bucket = "cachet-archive-prod";

    private readonly string _tempRoot;
    private readonly string _odriveRoot;
    private readonly string _b2Root;
    private readonly IBucketMirrorRootRepository _roots = Substitute.For<IBucketMirrorRootRepository>();

    public MirrorFileReaderOpenReadTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cas040-open-read-" + Guid.NewGuid().ToString("N"));
        _odriveRoot = Path.Combine(_tempRoot, "mirror");
        _b2Root = Path.Combine(_tempRoot, "b2mirror");
        Directory.CreateDirectory(_odriveRoot);
        Directory.CreateDirectory(_b2Root);

        _roots.GetRootPathAsync(Bucket, "ODrive", Arg.Any<CancellationToken>()).Returns(_odriveRoot);
        _roots.GetRootPathAsync(Bucket, "B2", Arg.Any<CancellationToken>()).Returns(_b2Root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                // Remove junctions first so Delete(recursive) never follows them.
                foreach (var dir in Directory.GetDirectories(_tempRoot, "*", SearchOption.AllDirectories)
                             .Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)))
                {
                    Directory.Delete(dir);
                }

                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private MirrorFileReader Sut() => new(_roots);

    private static FileIndexRecord Record(string source, string relativePath, string? bucket = Bucket) =>
        FileViewerTestFixtures.Record(fileIndexId: 1, extension: "txt", source: source, bucketName: bucket, relativePath: relativePath);

    private static string WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static async Task<string> ReadAsync(MirrorFileHandle handle)
    {
        using var reader = new StreamReader(handle.Stream!, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    // -----------------------------------------------------------------------------------------
    // SC-11
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC11_ResolvesBySourceAndRejectsTraversal()
    {
        // Same relative path under both mirrors, different content.
        const string relative = @"contracts\2026\agreement.txt";
        WriteFile(_odriveRoot, relative, "odrive-copy");
        WriteFile(_b2Root, relative, "b2-copy");

        // ODrive -> odrive_root_path.
        await using (var odrive = await Sut().OpenReadAsync(Record("ODrive", relative), CancellationToken.None))
        {
            odrive.Outcome.Should().Be(MirrorFileOpenOutcome.Opened);
            (await ReadAsync(odrive)).Should().Be("odrive-copy");
            odrive.Length.Should().Be("odrive-copy".Length);
        }

        // B2 -> b2_mirror_root_path.
        await using (var b2 = await Sut().OpenReadAsync(Record("B2", relative), CancellationToken.None))
        {
            b2.Outcome.Should().Be(MirrorFileOpenOutcome.Opened);
            (await ReadAsync(b2)).Should().Be("b2-copy");
        }

        await _roots.Received(1).GetRootPathAsync(Bucket, "ODrive", Arg.Any<CancellationToken>());
        await _roots.Received(1).GetRootPathAsync(Bucket, "B2", Arg.Any<CancellationToken>());

        // ..\..\Windows\win.ini is rejected (V7) — never MissingFromMirror, never opened.
        using var traversal = await Sut().OpenReadAsync(Record("ODrive", @"..\..\Windows\win.ini"), CancellationToken.None);
        traversal.Outcome.Should().Be(MirrorFileOpenOutcome.PathRejected);
        traversal.Stream.Should().BeNull();

        // An absolute path to a file that really exists outside the root is rejected without being opened.
        var outside = WriteFile(_tempRoot, @"outside\secret.txt", "secret");
        var absolute = await Sut().OpenReadAsync(Record("ODrive", outside), CancellationToken.None);
        absolute.Outcome.Should().Be(MirrorFileOpenOutcome.PathRejected);
        absolute.Stream.Should().BeNull();
        File.ReadAllText(outside).Should().Be("secret");

        // Step 6: the handle is opened with FileShare.Delete — a concurrent delete is not blocked.
        var deletable = WriteFile(_odriveRoot, "deletable.txt", "bytes-at-open");
        await using var handle = await Sut().OpenReadAsync(Record("ODrive", "deletable.txt"), CancellationToken.None);
        handle.Outcome.Should().Be(MirrorFileOpenOutcome.Opened);
        var delete = () => File.Delete(deletable);
        delete.Should().NotThrow("Permanent Delete / Purge must never be blocked by an open view (§12)");
        (await ReadAsync(handle)).Should().Be("bytes-at-open", "the stream completes with the bytes that were opened (§6.4)");
    }

    // -----------------------------------------------------------------------------------------
    // V7 (plus the V6/V8 outcomes the same method reports)
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("dotdot", MirrorFileOpenOutcome.PathRejected)]
    [InlineData("absolute", MirrorFileOpenOutcome.PathRejected)]
    [InlineData("sibling-prefix", MirrorFileOpenOutcome.PathRejected)]
    [InlineData("reparse-directory", MirrorFileOpenOutcome.PathRejected)]
    [InlineData("reparse-at-root-child", MirrorFileOpenOutcome.PathRejected)]
    [InlineData("invalid-characters", MirrorFileOpenOutcome.PathRejected)]
    // Controls: non-escaping failures are reported as their own outcome, never as PathRejected.
    [InlineData("missing-file", MirrorFileOpenOutcome.MissingFromMirror)]
    [InlineData("no-bucket", MirrorFileOpenOutcome.MappingMissing)]
    [InlineData("mapping-throws", MirrorFileOpenOutcome.MappingMissing)]
    [InlineData("empty-root", MirrorFileOpenOutcome.MappingMissing)]
    public async Task Test_V7_PathEscapingMirrorRootIsRejected(string scenario, MirrorFileOpenOutcome expected)
    {
        string relativePath;
        string? bucket = Bucket;
        switch (scenario)
        {
            case "missing-file":
                relativePath = @"contracts\missing.txt";
                break;
            case "no-bucket":
                relativePath = "a.txt";
                bucket = null;
                break;
            case "mapping-throws":
                relativePath = "a.txt";
                _roots.GetRootPathAsync(Bucket, "ODrive", Arg.Any<CancellationToken>())
                    .ThrowsAsync(new InvalidOperationException("No active mapping."));
                break;
            case "empty-root":
                relativePath = "a.txt";
                _roots.GetRootPathAsync(Bucket, "ODrive", Arg.Any<CancellationToken>()).Returns(" ");
                break;
            case "dotdot":
                WriteFile(_tempRoot, "escaped.txt", "x");
                relativePath = @"contracts\..\..\escaped.txt";
                break;
            case "absolute":
                relativePath = WriteFile(_tempRoot, "abs.txt", "x");
                break;
            case "sibling-prefix":
                // Root ...\mirror vs file in ...\mirror2 — a naive StartsWith(root) would accept it.
                WriteFile(Path.Combine(_tempRoot, "mirror2"), "x.txt", "x");
                relativePath = @"..\mirror2\x.txt";
                break;
            case "reparse-directory":
                {
                    // A junction inside the mirror that points outside it.
                    var target = Path.Combine(_tempRoot, "junction-target");
                    WriteFile(target, "leak.txt", "leak");
                    CreateJunction(Path.Combine(_odriveRoot, "sub", "link"), target);
                    relativePath = @"sub\link\leak.txt";
                    break;
                }
            case "reparse-at-root-child":
                {
                    var target = Path.Combine(_tempRoot, "junction-target-2");
                    WriteFile(target, "leak.txt", "leak");
                    CreateJunction(Path.Combine(_odriveRoot, "link"), target);
                    relativePath = @"link\leak.txt";
                    break;
                }
            default:
                relativePath = "bad\0name.txt";
                break;
        }

        var handle = await Sut().OpenReadAsync(Record("ODrive", relativePath, bucket), CancellationToken.None);

        handle.Outcome.Should().Be(expected, $"scenario '{scenario}'");
        handle.Stream.Should().BeNull();
        handle.Length.Should().Be(0);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint)
            .Should().BeTrue("the test fixture must actually create a junction (mklink /J) to prove reparse-point rejection");
    }
}
