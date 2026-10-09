using Cachet.ArchiveSync.Api.Storage;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="MirrorFileReader"/> (CAS-012 §5/§13 "MirrorFileReaderTests" — successful
/// read; missing-file/I/O-exception path). Unlike <see cref="IBucketMirrorRootRepository"/>/
/// <see cref="Cachet.ArchiveSync.Api.Repositories.FileIndexRepository"/> (both Dapper/SQL-backed and
/// therefore covered only by real-DB integration tests per this project's convention), this class
/// has no database dependency of its own — <see cref="IBucketMirrorRootRepository"/> is mocked and
/// the filesystem read is exercised for real against a temp directory, making full unit coverage
/// possible.
/// </summary>
public sealed class MirrorFileReaderTests : IDisposable
{
    private readonly string _tempRoot;

    public MirrorFileReaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cas012-mirror-file-reader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    // ---- Supports SC-1: a resolved mirror root + relative path reads the expected bytes -----------

    [Fact]
    public async Task Test_SC1_ReadAllBytesAsync_ResolvesRootAndReadsFile()
    {
        // Arrange
        var bucketMirrorRootRepository = Substitute.For<IBucketMirrorRootRepository>();
        bucketMirrorRootRepository
            .GetRootPathAsync("cachet-archive-prod", "B2", Arg.Any<CancellationToken>())
            .Returns(_tempRoot);

        var relativePath = Path.Combine("internal", "notes", "meeting-notes-q3.docx");
        var fullPath = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var expectedBytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(fullPath, expectedBytes);

        var sut = new MirrorFileReader(bucketMirrorRootRepository);

        // Act
        var actualBytes = await sut.ReadAllBytesAsync("B2", "cachet-archive-prod", relativePath, CancellationToken.None);

        // Assert
        actualBytes.Should().Equal(expectedBytes);
        await bucketMirrorRootRepository.Received(1).GetRootPathAsync("cachet-archive-prod", "B2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC1_ReadAllBytesAsync_ODriveSource_ResolvesViaSourceParameter()
    {
        // Arrange — proves the source parameter is passed through to the root-path repository
        // unchanged (ODrive vs B2 selects a different mirror root at that layer, CAS-011 §7).
        var bucketMirrorRootRepository = Substitute.For<IBucketMirrorRootRepository>();
        bucketMirrorRootRepository
            .GetRootPathAsync("cachet-archive-prod", "ODrive", Arg.Any<CancellationToken>())
            .Returns(_tempRoot);

        const string relativePath = "contracts/2019/agreement-004.pdf";
        var fullPath = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, new byte[] { 9, 8, 7 });

        var sut = new MirrorFileReader(bucketMirrorRootRepository);

        // Act
        var actualBytes = await sut.ReadAllBytesAsync("ODrive", "cachet-archive-prod", relativePath, CancellationToken.None);

        // Assert
        actualBytes.Should().Equal(9, 8, 7);
    }

    // ---- Supports SC-3/V10: a missing file surfaces as an exception the caller treats as a skip ----

    [Fact]
    public async Task Test_SC3_ReadAllBytesAsync_MissingFile_ThrowsForCallerToSkip()
    {
        // Arrange — the resolved root exists, but the specific relative path does not.
        var bucketMirrorRootRepository = Substitute.For<IBucketMirrorRootRepository>();
        bucketMirrorRootRepository
            .GetRootPathAsync("cachet-archive-prod", "B2", Arg.Any<CancellationToken>())
            .Returns(_tempRoot);
        var sut = new MirrorFileReader(bucketMirrorRootRepository);

        // The parent directory exists but the file itself does not, so the failure is specifically
        // a missing file (FileNotFoundException), not a missing directory.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "does", "not"));

        // Act
        var act = async () => await sut.ReadAllBytesAsync("B2", "cachet-archive-prod", "does/not/exist.txt", CancellationToken.None);

        // Assert — KeywordSearchTaskProcessor's TryMatchContentCandidateAsync (CAS-012 §7 V10)
        // catches any non-OperationCanceledException here and treats it as a skip, not a task
        // failure — this test proves the exception is actually thrown for that catch to observe.
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Test_SC3_ReadAllBytesAsync_UnresolvableMapping_PropagatesRepositoryException()
    {
        // Arrange — IBucketMirrorRootRepository itself throws when no mapping row exists for the
        // bucket (its own documented contract); MirrorFileReader must not swallow this — the
        // caller's skip-not-fail handling depends on the exception reaching it.
        var bucketMirrorRootRepository = Substitute.For<IBucketMirrorRootRepository>();
        bucketMirrorRootRepository
            .GetRootPathAsync("unknown-bucket", "B2", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("No active odrive_bucket_mapping row found for the requested bucket."));
        var sut = new MirrorFileReader(bucketMirrorRootRepository);

        // Act
        var act = async () => await sut.ReadAllBytesAsync("B2", "unknown-bucket", "some/file.txt", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
