using Cachet.ArchiveSync.Api.Storage;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="ODriveFileReader"/> (CAS-013 §5/§13 "ODriveFileReaderTests" —
/// reader-level SC-1 and V1). <see cref="IBucketRootMappingRepository"/> is mocked; the filesystem
/// read is exercised for real against a temp directory per bucket, matching the pattern
/// <see cref="MirrorFileReaderTests"/> (CAS-012) already established for the sibling reader.
/// </summary>
public sealed class ODriveFileReaderTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;

    public ODriveFileReaderTests()
    {
        _rootA = Path.Combine(Path.GetTempPath(), "cas013-odrive-file-reader-tests-a-" + Guid.NewGuid().ToString("N"));
        _rootB = Path.Combine(Path.GetTempPath(), "cas013-odrive-file-reader-tests-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootA))
        {
            Directory.Delete(_rootA, recursive: true);
        }

        if (Directory.Exists(_rootB))
        {
            Directory.Delete(_rootB, recursive: true);
        }
    }

    // ---- CAS-013 SC-1 (reader-level): two bucket fixtures, two roots — each bucket's read resolves --
    // ---- against its OWN root, not a single shared root -----------------------------------------------

    [Fact]
    public async Task Test_SC1_SyncCopyReadsFromCorrectPerBucketRoot()
    {
        // Arrange — two distinct buckets, each mapped to its own root via IBucketRootMappingRepository
        // (CAS-011's dbo.odrive_bucket_mapping table), with a file at the SAME relative path under
        // each root, holding DIFFERENT bytes — proving the correct root was chosen, not merely that
        // some file was read.
        var bucketRootMappingRepository = Substitute.For<IBucketRootMappingRepository>();
        bucketRootMappingRepository.GetRootPathAsync("cachet-archive-bucket-a", Arg.Any<CancellationToken>()).Returns(_rootA);
        bucketRootMappingRepository.GetRootPathAsync("cachet-archive-bucket-b", Arg.Any<CancellationToken>()).Returns(_rootB);

        const string relativePath = "finance/2026/shared-relative-path.xlsx";
        var fullPathA = Path.Combine(_rootA, relativePath);
        var fullPathB = Path.Combine(_rootB, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPathA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPathB)!);
        var bytesA = new byte[] { 1, 1, 1 };
        var bytesB = new byte[] { 2, 2, 2, 2 };
        await File.WriteAllBytesAsync(fullPathA, bytesA);
        await File.WriteAllBytesAsync(fullPathB, bytesB);

        var sut = new ODriveFileReader(bucketRootMappingRepository);

        // Act
        var actualA = await sut.ReadAllBytesAsync("cachet-archive-bucket-a", relativePath, CancellationToken.None);
        var actualB = await sut.ReadAllBytesAsync("cachet-archive-bucket-b", relativePath, CancellationToken.None);

        // Assert — each bucket's own root was read, not a single shared root.
        actualA.Should().Equal(bytesA);
        actualB.Should().Equal(bytesB);
        await bucketRootMappingRepository.Received(1).GetRootPathAsync("cachet-archive-bucket-a", Arg.Any<CancellationToken>());
        await bucketRootMappingRepository.Received(1).GetRootPathAsync("cachet-archive-bucket-b", Arg.Any<CancellationToken>());
    }

    // ---- CAS-013 V1: an unmapped bucket throws a clear, descriptive exception (not a generic --------
    // ---- FileNotFoundException) — caught by the caller's existing SyncActionFailedLocal path --------

    [Fact]
    public async Task Test_V1_UnmappedBucketFailsThatFileLocalReadNotWholeBatch()
    {
        // Arrange — no active odrive_bucket_mapping row exists for this bucket (repository contract:
        // returns null, does not throw — CAS-013 §5/IBucketRootMappingRepository's XML doc).
        var bucketRootMappingRepository = Substitute.For<IBucketRootMappingRepository>();
        bucketRootMappingRepository.GetRootPathAsync("unmapped-bucket", Arg.Any<CancellationToken>()).Returns((string?)null);
        var sut = new ODriveFileReader(bucketRootMappingRepository);

        // Act
        var act = async () => await sut.ReadAllBytesAsync("unmapped-bucket", "finance/2026/orphaned.xlsx", CancellationToken.None);

        // Assert — a clear, descriptive InvalidOperationException (not a generic FileNotFoundException
        // that would otherwise leak a resolved local path via its Message), so the caller's
        // SyncActionFailedLocal per-file failure path (SyncCopyTaskProcessor/SyncUpdateTaskProcessor)
        // can catch and classify it without inspecting a filesystem path.
        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Should().NotBeOfType<FileNotFoundException>();
        exception.Which.Message.Should().NotContain(_rootA);
        exception.Which.Message.Should().NotContain(_rootB);
    }
}
