using Cachet.ArchiveSync.Api.Storage;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-062 §10 SC-1 – SC-5 — <see cref="B2MirrorFileRemover"/> against a real temporary directory
/// standing in for the B2 mirror root, with an NSubstitute <see cref="IBucketMirrorRootRepository"/>.
/// </summary>
public sealed class B2MirrorFileRemoverTests : IDisposable
{
    private const string Bucket = "tc-test-Server";

    private readonly string _parent = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "cas062-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _root;
    private readonly IBucketMirrorRootRepository _roots = Substitute.For<IBucketMirrorRootRepository>();

    public B2MirrorFileRemoverTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_parent, "B2mirror")).FullName;
        _roots.GetRootPathAsync(Bucket, "B2", Arg.Any<CancellationToken>()).Returns(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a per-test temp directory.
        }
    }

    private B2MirrorFileRemover CreateSut(string? bucket = Bucket) =>
        new(_roots, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = bucket })
            .Build());

    private string WriteFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public async Task Test_CAS062_Remover_ExistingFile_Removed()
    {
        var path = WriteFile("D/Shares/Operations/Gold Chain/box.pdf");

        var result = await CreateSut().RemoveAsync("D/Shares/Operations/Gold Chain/box.pdf", CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Removed);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Test_CAS062_Remover_MissingFile_NotPresent()
    {
        var result = await CreateSut().RemoveAsync("D/Shares/Operations/never-mirrored.pdf", CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.NotPresent);
        result.FailureReason.Should().BeNull();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("D/../../outside.txt")]
    public async Task Test_CAS062_Remover_OutsideRoot_FailedNothingDeleted(string objectName)
    {
        // A file that would be hit if containment failed — next to the mirror root, not inside it.
        var outside = Path.Combine(_parent, "outside.txt");
        File.WriteAllBytes(outside, new byte[] { 9 });

        var result = await CreateSut().RemoveAsync(objectName, CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
        File.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public async Task Test_CAS062_Remover_RootedName_FailedNothingDeleted()
    {
        var outside = Path.Combine(_parent, "rooted.txt");
        File.WriteAllBytes(outside, new byte[] { 9 });

        var result = await CreateSut().RemoveAsync(outside.Replace('\\', '/'), CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
        File.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public async Task Test_CAS062_Remover_LockedFile_Failed()
    {
        var path = WriteFile("D/Shares/Operations/locked.pdf");
        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = async () => await CreateSut().RemoveAsync("D/Shares/Operations/locked.pdf", CancellationToken.None);

        (await act.Should().NotThrowAsync()).Which.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Test_CAS062_Remover_ReadOnlyFile_Failed()
    {
        var path = WriteFile("D/Shares/Operations/readonly.pdf");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await CreateSut().RemoveAsync("D/Shares/Operations/readonly.pdf", CancellationToken.None);

            result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Test_CAS062_Remover_SiblingFolderOfRoot_FailedNothingDeleted()
    {
        // "<parent>\B2mirror2" must not count as inside "<parent>\B2mirror".
        var sibling = Directory.CreateDirectory(Path.Combine(_parent, "B2mirror2")).FullName;
        var victim = Path.Combine(sibling, "x.txt");
        File.WriteAllBytes(victim, new byte[] { 9 });

        var result = await CreateSut().RemoveAsync("../B2mirror2/x.txt", CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
        File.Exists(victim).Should().BeTrue();
    }

    [Theory]
    [InlineData("D/Shares/Operations/b\\c.txt")]
    [InlineData("D/Shares/Operations/line\nbreak.txt")]
    public async Task Test_CAS062_Remover_BackslashOrControlCharName_Failed(string objectName)
    {
        // Review F-3: "a/b\c.txt" would otherwise resolve to the local copy of "a/b/c.txt".
        var other = WriteFile("D/Shares/Operations/b/c.txt");

        var result = await CreateSut().RemoveAsync(objectName, CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
        File.Exists(other).Should().BeTrue();
    }

    [Theory]
    [InlineData("D/Shares/Operations/./b.pdf")]
    [InlineData("D/Shares/Operations//b.pdf")]
    [InlineData("D/Shares/x/../Operations/b.pdf")]
    [InlineData("D/Shares/Operations/b.pdf::$DATA")]
    [InlineData("D/Shares/Operations/b.pdf.")]
    [InlineData("D/Shares/Operations/b.pdf ")]
    [InlineData("D/Shares/Operations./b.pdf")]
    [InlineData("D/Shares/Operations/b<1>.pdf")]
    [InlineData("D/Shares/Operations/b?.pdf")]
    [InlineData("D/Shares/Operations/b|.pdf")]
    [InlineData("D/Shares/Operations/\"b\".pdf")]
    [InlineData("D/Shares/Operations/b*.pdf")]
    public async Task Test_CAS062_Remover_UnsafeNameFoldingOntoOtherFile_FailedOtherKept(string objectName)
    {
        // Review S-1: each name would resolve (or fold, on Windows) onto another object's mirror
        // file "D/Shares/Operations/b.pdf" — it must be refused, never deleted.
        var other = WriteFile("D/Shares/Operations/b.pdf");

        var result = await CreateSut().RemoveAsync(objectName, CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
        File.Exists(other).Should().BeTrue();
    }

    [Fact]
    public async Task Test_CAS062_Remover_DriveRoot_ContainmentAccepted()
    {
        // A drive-root mapping ("Q:\") keeps its own separator: the key is inside the root, so the
        // result is NotPresent (no such file), not a containment Failed.
        _roots.GetRootPathAsync(Bucket, "B2", Arg.Any<CancellationToken>()).Returns(@"Q:\");

        var result = await CreateSut().RemoveAsync("D/Shares/Operations/none-" + Guid.NewGuid().ToString("N") + ".pdf", CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.NotPresent);
    }

    [Fact]
    public async Task Test_CAS062_Remover_JunctionInsideRoot_FailedTargetKept()
    {
        // A junction inside the mirror pointing outside it must not redirect the delete.
        var target = Directory.CreateDirectory(Path.Combine(_parent, "elsewhere")).FullName;
        var victim = Path.Combine(target, "x.txt");
        File.WriteAllBytes(victim, new byte[] { 9 });
        var link = Path.Combine(_root, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
        }

        // Fail visibly rather than pass without verifying anything (review note). "mklink /J" needs
        // no elevation on Windows, so this holds on developer machines and Windows build agents.
        Directory.Exists(link).Should().BeTrue("the test needs a junction (mklink /J) to verify the reparse-point check");

        try
        {
            var result = await CreateSut().RemoveAsync("link/x.txt", CancellationToken.None);

            result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
            File.Exists(victim).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(link); // removes the junction only, not the target
        }
    }

    [Fact]
    public async Task Test_CAS062_Remover_UsesB2MirrorRootOnly()
    {
        WriteFile("D/Shares/Operations/a.pdf");

        await CreateSut().RemoveAsync("D/Shares/Operations/a.pdf", CancellationToken.None);

        await _roots.Received(1).GetRootPathAsync(Bucket, "B2", Arg.Any<CancellationToken>());
        await _roots.DidNotReceive().GetRootPathAsync(Arg.Any<string>(), "ODrive", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_CAS062_Remover_NoMapping_Failed()
    {
        _roots.GetRootPathAsync(Bucket, "B2", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("No active odrive_bucket_mapping row found for the requested bucket."));

        var result = await CreateSut().RemoveAsync("D/Shares/Operations/a.pdf", CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
    }

    [Fact]
    public async Task Test_CAS062_Remover_NoBucketConfigured_Failed()
    {
        var result = await CreateSut(bucket: null).RemoveAsync("D/Shares/Operations/a.pdf", CancellationToken.None);

        result.Outcome.Should().Be(B2MirrorRemoveOutcome.Failed);
        await _roots.DidNotReceive().GetRootPathAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
