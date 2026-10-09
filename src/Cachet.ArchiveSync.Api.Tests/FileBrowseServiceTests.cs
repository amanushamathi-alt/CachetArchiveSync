using System.Text.Json;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-043 §13 - <see cref="FileBrowseService"/>: V1, V2 and V6 guards, SC-7 breadcrumb/parent
/// mapping, SC-12 prefix handling. V4 (non-boolean foldersOnly) is parsed by the controller before
/// the service is reached, so it is proven over HTTP in
/// <c>FilesBrowseControllerTests.Test_V4_NonBooleanFoldersOnlyRejected</c>. Repository and prefix
/// provider are substituted.
/// </summary>
public sealed class FileBrowseServiceTests
{
    private const string Bucket = "Cachet-Server-C";
    private const string Prefix = "D/Shares/Operations";
    private const int UserId = 7;

    private readonly IFileBrowseRepository _repository = Substitute.For<IFileBrowseRepository>();
    private readonly IB2PathPrefixProvider _prefixProvider = Substitute.For<IB2PathPrefixProvider>();

    public FileBrowseServiceTests()
    {
        _prefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Prefix);
        ReturnsPage(NonEmptyPage());
    }

    private FileBrowseService Build(string? bucketName = Bucket) =>
        new(_repository, _prefixProvider,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = bucketName })
                .Build(),
            NullLogger<FileBrowseService>.Instance);

    private void ReturnsPage(FileBrowsePage page) =>
        _repository.BrowseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(page);

    private static FileBrowsePage NonEmptyPage() => new()
    {
        Folders = new[] { new FileBrowseFolderRow("175 S. Lake", new DateTimeOffset(2024, 10, 29, 19, 36, 6, TimeSpan.Zero)) },
        Files = new[] { new FileBrowseFileRow(100001, "1099 Filing.xlsx", "XLSX", 16634, new DateTimeOffset(2022, 2, 7, 17, 35, 35, TimeSpan.Zero)) },
        TotalFolders = 1,
        TotalFiles = 1
    };

    private Task<FileBrowseResult> BrowseAsync(
        FileBrowseService sut, string? folder = "", bool foldersOnly = false) =>
        sut.BrowseAsync(folder, foldersOnly, UserId, CancellationToken.None);

    private async Task AssertNothingQueriedAsync() =>
        await _repository.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default!, default!, default, default);

    // CAS-046 SC-4
    [Fact]
    public async Task Test_SC4_ServiceMapsHasSubfolders()
    {
        var modified = new DateTimeOffset(2024, 10, 29, 19, 36, 6, TimeSpan.Zero);
        ReturnsPage(new FileBrowsePage
        {
            Folders = new[]
            {
                new FileBrowseFolderRow("Branch", modified, true),
                new FileBrowseFolderRow("Leaf", modified, false),
                new FileBrowseFolderRow("Unspecified", modified),
            },
            Files = Array.Empty<FileBrowseFileRow>(),
            TotalFolders = 3,
            TotalFiles = 0
        });

        var response = (await BrowseAsync(Build(), "")).Response!;

        response.Folders.ToDictionary(f => f.Name, f => f.HasSubfolders).Should().Equal(
            new Dictionary<string, bool> { ["Branch"] = true, ["Leaf"] = false, ["Unspecified"] = true });
    }

    // SC-14 (abort surfaced by SqlClient as a non-cancellation exception is cancellation, not BrowseFailed)
    [Fact]
    public async Task Test_SC14_AbortSurfacedAsDatabaseExceptionIsCancellationNotFailure()
    {
        using var cts = new CancellationTokenSource();
        _repository.BrowseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<FileBrowsePage>>(_ =>
            {
                cts.Cancel();
                throw new InvalidOperationException("Operation cancelled by user.");
            });
        var sut = Build();

        var act = async () => await sut.BrowseAsync("", false, UserId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // V1
    [Fact]
    public async Task Test_V1_FolderOverMaxLengthRejected()
    {
        var sut = Build();

        var atLimit = await BrowseAsync(sut, new string('a', 1000 - Prefix.Length - 1));
        var tooLong = await BrowseAsync(sut, new string('a', 1001));
        // Within 1000 on its own, but the composed key path (prefix + folder) would exceed the SP's NVARCHAR(1000).
        var composedTooLong = await BrowseAsync(sut, new string('a', 1000 - Prefix.Length));

        atLimit.Outcome.Should().Be(FileBrowseOutcome.Success);
        tooLong.Outcome.Should().Be(FileBrowseOutcome.ValidationFailed);
        composedTooLong.Outcome.Should().Be(FileBrowseOutcome.ValidationFailed);
        await _repository.Received(1).BrowseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // V2
    [Theory]
    [InlineData("/Accounts")]
    [InlineData("Accounts/")]
    [InlineData("Accounts//2026")]
    [InlineData("Accounts\\2026")]
    [InlineData("Accounts/../Secret")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("Accounts/./2026")]
    [InlineData("Accounts/2026/..")]
    [InlineData("Acc\u0001ounts")]
    [InlineData("Acc\nounts")]
    [InlineData("Acc\tounts")]
    public async Task Test_V2_MalformedOrTraversalFolderRejected(string folder)
    {
        var result = await BrowseAsync(Build(), folder);

        result.Outcome.Should().Be(FileBrowseOutcome.ValidationFailed);
        result.Response.Should().BeNull();
        await AssertNothingQueriedAsync();
    }

    // V6
    [Theory]
    [InlineData(null, FileBrowseOutcome.BucketNotConfigured)]
    [InlineData("", FileBrowseOutcome.BucketNotConfigured)]
    [InlineData("   ", FileBrowseOutcome.BucketNotConfigured)]
    [InlineData(Bucket, FileBrowseOutcome.MappingMissing)]
    public async Task Test_V6_MissingBucketConfigOrMappingReturns500(string? bucketName, FileBrowseOutcome expected)
    {
        // A null prefix is the provider's "no active mapping row" signal.
        _prefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await BrowseAsync(Build(bucketName));

        result.Outcome.Should().Be(expected);
        result.Response.Should().BeNull();
        await AssertNothingQueriedAsync();
        if (expected == FileBrowseOutcome.BucketNotConfigured)
        {
            await _prefixProvider.DidNotReceiveWithAnyArgs().GetPrefixAsync(default!, default);
        }
    }

    // SC-7
    [Fact]
    public async Task Test_SC7_BreadcrumbAndParentMatchFolderPath()
    {
        var sut = Build();

        var nested = (await BrowseAsync(sut, "Accounts Payable - Cachet/2026/Q3")).Response!;
        var oneDeep = (await BrowseAsync(sut, "Accounts Payable - Cachet")).Response!;
        var root = (await BrowseAsync(sut, "")).Response!;

        nested.Folder.Should().Be("Accounts Payable - Cachet/2026/Q3");
        nested.Parent.Should().Be("Accounts Payable - Cachet/2026");
        nested.Breadcrumb.Select(b => (b.Name, b.Path)).Should().Equal(
            ("Accounts Payable - Cachet", "Accounts Payable - Cachet"),
            ("2026", "Accounts Payable - Cachet/2026"),
            ("Q3", "Accounts Payable - Cachet/2026/Q3"));

        oneDeep.Parent.Should().Be(string.Empty, "the immediate ancestor of a first-level folder is the root");
        oneDeep.Breadcrumb.Should().ContainSingle().Which.Path.Should().Be("Accounts Payable - Cachet");

        root.Parent.Should().BeNull();
        root.Breadcrumb.Should().BeEmpty();
    }

    // SC-12
    [Fact]
    public async Task Test_SC12_RootIsRelativeToBucketPrefixAndHidesIt()
    {
        var sut = Build();

        var root = (await BrowseAsync(sut, "", foldersOnly: false)).Response!;
        var nested = (await BrowseAsync(sut, "175 S. Lake")).Response!;
        await BrowseAsync(sut, "175 S. Lake", foldersOnly: true);

        // The SP is asked for the full key path; the fixed source/bucket pass through.
        await _repository.Received(1).BrowseAsync("B2", Bucket, Prefix, false, Arg.Any<CancellationToken>());
        await _repository.Received(1).BrowseAsync("B2", Bucket, Prefix + "/175 S. Lake", false, Arg.Any<CancellationToken>());
        await _repository.Received(1).BrowseAsync("B2", Bucket, Prefix + "/175 S. Lake", true, Arg.Any<CancellationToken>());

        root.RootName.Should().Be("Operations");
        root.Folders.Should().ContainSingle().Which.Path.Should().Be("175 S. Lake");
        var file = root.Files.Should().ContainSingle().Subject;
        file.Name.Should().Be("1099 Filing.xlsx");
        file.Extension.Should().Be("xlsx", "the stored upper-case extension is returned lower-cased");
        nested.Folders.Single().Path.Should().Be("175 S. Lake/175 S. Lake");

        foreach (var response in new[] { root, nested })
        {
            var json = JsonSerializer.Serialize(response);
            json.Should().NotContain("D/Shares");
            json.Should().NotContain(Prefix);
        }
    }

    // SC-5 (service level): totals equal the number of items returned and nothing is dropped.
    [Fact]
    public async Task Test_SC5_ListsAreSortedAndNothingIsTruncated()
    {
        var files = Enumerable.Range(1, 2500)
            .Select(i => new FileBrowseFileRow(i, $"f{i:D4}.txt", "TXT", 10, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            .ToList();
        ReturnsPage(new FileBrowsePage { Folders = Array.Empty<FileBrowseFolderRow>(), Files = files, TotalFolders = 0, TotalFiles = 2500 });

        var response = (await BrowseAsync(Build(), "")).Response!;

        response.Files.Should().HaveCount(2500);
        response.TotalFiles.Should().Be(2500);
        response.TotalFolders.Should().Be(response.Folders.Count);
        response.Files.Select(f => f.Name).Should().BeInAscendingOrder();
    }

    // V5 with foldersOnly: a leaf folder (files, no subfolders) is not a false 404; an unknown one is 404.
    [Fact]
    public async Task Test_V5_FoldersOnlyLeafFolderIsNot404_UnknownFolderIs()
    {
        var empty = new FileBrowsePage { Folders = Array.Empty<FileBrowseFolderRow>(), Files = Array.Empty<FileBrowseFileRow>(), TotalFolders = 0, TotalFiles = 0 };
        var leaf = empty with
        {
            Files = new[] { new FileBrowseFileRow(1, "a.txt", "TXT", 1, DateTimeOffset.UnixEpoch) },
            TotalFiles = 1
        };
        var sut = Build();

        _repository.BrowseAsync("B2", Bucket, Prefix + "/Leaf", true, Arg.Any<CancellationToken>()).Returns(empty);
        _repository.BrowseAsync("B2", Bucket, Prefix + "/Leaf", false, Arg.Any<CancellationToken>()).Returns(leaf);
        _repository.BrowseAsync("B2", Bucket, Prefix + "/Nope", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(empty);

        var leafResult = await BrowseAsync(sut, "Leaf", foldersOnly: true);
        var unknownResult = await BrowseAsync(sut, "Nope", foldersOnly: true);

        leafResult.Outcome.Should().Be(FileBrowseOutcome.Success);
        leafResult.Response!.Folders.Should().BeEmpty();
        leafResult.Response.Files.Should().BeEmpty("foldersOnly returns no files");
        leafResult.Response.TotalFiles.Should().Be(0);
        unknownResult.Outcome.Should().Be(FileBrowseOutcome.FolderNotFound);
    }
}
