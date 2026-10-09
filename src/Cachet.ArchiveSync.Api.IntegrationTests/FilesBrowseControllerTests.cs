using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Api.IntegrationTests.Files;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-043 §13 — HTTP tests for <c>GET /files/browse</c> against the REAL <c>Program.cs</c> pipeline
/// (<see cref="BrowseHttpHarness"/>: real <c>FileBrowseService</c> + <c>FilesController</c>,
/// substituted <c>IFileBrowseRepository</c> / <c>IB2PathPrefixProvider</c>). SC-8, SC-9, SC-10, SC-14,
/// V4, V5, V9.
/// </summary>
public sealed class FilesBrowseControllerTests : IAsyncLifetime
{
    private const string ErrorBase = "https://cachet.tracecounts.internal/errors/";

    private BrowseHttpHarness _h = null!;

    public Task InitializeAsync()
    {
        _h = new BrowseHttpHarness();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private void ReturnsPage(FileBrowsePage page) =>
        _h.Repository.BrowseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(page);

    private static async Task<string> ProblemTypeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("type").GetString()!;
    }

    // SC-8
    [Fact]
    public async Task Test_SC8_ResponseExposesNoServerPathOrCredential()
    {
        var cookie = await _h.LoginAsync();
        ReturnsPage(new FileBrowsePage
        {
            Folders = new[] { new FileBrowseFolderRow("175 S. Lake", new DateTimeOffset(2024, 10, 29, 19, 36, 6, TimeSpan.Zero)) },
            Files = new[] { new FileBrowseFileRow(100245, "1099 Filing.xlsx", "XLSX", 16634, new DateTimeOffset(2022, 2, 7, 17, 35, 35, TimeSpan.Zero)) },
            TotalFolders = 1,
            TotalFiles = 1
        });

        using var response = await _h.GetAsync("/files/browse", cookie);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("rootName").GetString().Should().Be("Operations");
        body.Should().NotContain(_h.Root.MirrorRoot);
        body.Should().NotContain("D/Shares");
        Regex.IsMatch(body, @"[A-Za-z]:\\\\").Should().BeFalse("no drive letter path may appear");
        body.Should().NotContain(BrowseHttpHarness.B2Token);
        body.Should().NotContain(BrowseHttpHarness.B2AppKey);
        body.Should().NotContain(BrowseHttpHarness.B2KeyId);
        body.Should().NotContain(BrowseHttpHarness.B2ApiHost);
        body.Should().NotContainEquivalentOf(FilesApiWebApplicationFactory.Bucket);
    }

    // CAS-046 SC-4
    [Fact]
    public async Task Test_SC4_BrowseJsonContainsHasSubfoldersOnEveryFolder()
    {
        var cookie = await _h.LoginAsync();
        var modified = new DateTimeOffset(2024, 10, 29, 19, 36, 6, TimeSpan.Zero);
        ReturnsPage(new FileBrowsePage
        {
            Folders = new[]
            {
                new FileBrowseFolderRow("Branch", modified, true),
                new FileBrowseFolderRow("Leaf", modified, false),
            },
            Files = Array.Empty<FileBrowseFileRow>(),
            TotalFolders = 2,
            TotalFiles = 0
        });

        using var response = await _h.GetAsync("/files/browse", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var folders = json.RootElement.GetProperty("folders").EnumerateArray().ToList();
        folders.Should().HaveCount(2);
        foreach (var folder in folders)
        {
            folder.TryGetProperty("hasSubfolders", out var flag).Should().BeTrue();
            flag.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        }
        folders.ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("hasSubfolders").GetBoolean())
            .Should().Equal(new Dictionary<string, bool> { ["Branch"] = true, ["Leaf"] = false });
    }

    // CAS-049: the additive BrowseFileItem fields must not appear in /files/browse JSON (null -> omitted).
    [Fact]
    public async Task Test_CAS049_FilesBrowseJsonOmitsSearchResultIdAndMatchStatus()
    {
        var cookie = await _h.LoginAsync();
        ReturnsPage(new FileBrowsePage
        {
            Folders = Array.Empty<FileBrowseFolderRow>(),
            Files = new[] { new FileBrowseFileRow(100245, "1099 Filing.xlsx", "XLSX", 16634, new DateTimeOffset(2022, 2, 7, 17, 35, 35, TimeSpan.Zero)) },
            TotalFolders = 0,
            TotalFiles = 1
        });

        using var response = await _h.GetAsync("/files/browse", cookie);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(body);
        var file = json.RootElement.GetProperty("files")[0];
        file.GetProperty("fileIndexId").GetInt32().Should().Be(100245);
        file.TryGetProperty("searchResultId", out _).Should().BeFalse();
        file.TryGetProperty("matchStatus", out _).Should().BeFalse();
    }

    // SC-9
    [Fact]
    public async Task Test_SC9_BrowseRouteDoesNotCollideWithFileIdRoute()
    {
        var cookie = await _h.LoginAsync();
        _h.SeedFile(123, "pdf");

        using var browse = await _h.GetAsync("/files/browse", cookie);
        browse.StatusCode.Should().Be(HttpStatusCode.OK);
        await _h.Repository.Received(1).BrowseAsync(
            "B2", FilesApiWebApplicationFactory.Bucket, BrowseHttpHarness.Prefix, false, Arg.Any<CancellationToken>());
        using (var browseJson = JsonDocument.Parse(await browse.Content.ReadAsStringAsync()))
        {
            browseJson.RootElement.TryGetProperty("folders", out _).Should().BeTrue("the browse action produced a FolderBrowseResponse");
        }

        _h.Repository.ClearReceivedCalls();
        using var metadata = await _h.GetAsync("/files/123?context=KeywordSearch", cookie);
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadataJson = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
        metadataJson.RootElement.GetProperty("fileIndexId").GetInt32().Should().Be(123, "GetMetadata handled the numeric id");
        await _h.Repository.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default!, default!, default, default);
    }

    // SC-10
    [Fact]
    public async Task Test_SC10_BrowseRequiresAuthenticatedSession()
    {
        using var response = await _h.GetAsync("/files/browse", cookie: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _h.Repository.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default!, default!, default, default);
    }

    // V4 - parsed in the controller; the service never sees a non-boolean.
    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("yes please")]
    public async Task Test_V4_NonBooleanFoldersOnlyRejected(string value)
    {
        var cookie = await _h.LoginAsync();

        using var response = await _h.GetAsync($"/files/browse?foldersOnly={Uri.EscapeDataString(value)}", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(response)).Should().Be(ErrorBase + "validation-failed");
        await _h.Repository.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default!, default!, default, default);
    }

    // V1, V2 and V6 reach the client as the section 6 status codes (controller mapping of the service outcomes).
    [Theory]
    [InlineData("/files/browse?folder=%2FAccounts")]
    [InlineData("/files/browse?folder=Accounts%2F..%2FSecret")]
    public async Task Test_V2_MalformedOrTraversalFolderRejected_ControllerMapsValidationFailedTo400(string url)
    {
        var cookie = await _h.LoginAsync();

        using var response = await _h.GetAsync(url, cookie);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(response)).Should().Be(ErrorBase + "validation-failed");
        await _h.Repository.DidNotReceiveWithAnyArgs().BrowseAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Test_V6_MissingBucketConfigOrMappingReturns500_ControllerMapsBothOutcomes()
    {
        var cookie = await _h.LoginAsync();
        _h.PrefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        using var noMapping = await _h.GetAsync("/files/browse", cookie);

        noMapping.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProblemTypeAsync(noMapping)).Should().Be(ErrorBase + "mirror-mapping-missing");

        await using var noBucket = new BrowseHttpHarness(bucketNameOverride: "");
        var bucketCookie = await noBucket.LoginAsync();
        using var noConfig = await noBucket.GetAsync("/files/browse", bucketCookie);

        noConfig.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProblemTypeAsync(noConfig)).Should().Be(ErrorBase + "bucket-not-configured");
        (await noConfig.Content.ReadAsStringAsync()).Should().NotContain("B2:BucketName");
    }

    // V5
    [Fact]
    public async Task Test_V5_UnknownFolderReturns404()
    {
        var cookie = await _h.LoginAsync();
        ReturnsPage(new FileBrowsePage { Folders = Array.Empty<FileBrowseFolderRow>(), Files = Array.Empty<FileBrowseFileRow>(), TotalFolders = 0, TotalFiles = 0 });

        using var response = await _h.GetAsync("/files/browse?folder=No%20Such%20Folder", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTypeAsync(response)).Should().Be(ErrorBase + "folder-not-found");
        await _h.Repository.Received(1).BrowseAsync(
            "B2", FilesApiWebApplicationFactory.Bucket, BrowseHttpHarness.Prefix + "/No Such Folder", false, Arg.Any<CancellationToken>());

        // An empty root is 200, not 404 (section 6).
        using var root = await _h.GetAsync("/files/browse", cookie);
        root.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // V5 with foldersOnly=true: a leaf folder (files, no subfolders) is 200, an unknown folder is 404.
    [Fact]
    public async Task Test_V5_FoldersOnlyLeafFolderIsNot404_UnknownFolderIs404()
    {
        var cookie = await _h.LoginAsync();
        var empty = new FileBrowsePage { Folders = Array.Empty<FileBrowseFolderRow>(), Files = Array.Empty<FileBrowseFileRow>(), TotalFolders = 0, TotalFiles = 0 };
        var leaf = empty with
        {
            Files = new[] { new FileBrowseFileRow(1, "a.txt", "TXT", 1, DateTimeOffset.UnixEpoch) },
            TotalFiles = 1
        };
        var leafPath = BrowseHttpHarness.Prefix + "/Leaf";
        _h.Repository.BrowseAsync("B2", FilesApiWebApplicationFactory.Bucket, leafPath, true, Arg.Any<CancellationToken>()).Returns(empty);
        _h.Repository.BrowseAsync("B2", FilesApiWebApplicationFactory.Bucket, leafPath, false, Arg.Any<CancellationToken>()).Returns(leaf);

        using var leafResponse = await _h.GetAsync("/files/browse?folder=Leaf&foldersOnly=true", cookie);
        using var unknownResponse = await _h.GetAsync("/files/browse?folder=Nope&foldersOnly=true", cookie);

        leafResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var json = JsonDocument.Parse(await leafResponse.Content.ReadAsStringAsync()))
        {
            json.RootElement.GetProperty("folders").GetArrayLength().Should().Be(0);
            json.RootElement.GetProperty("files").GetArrayLength().Should().Be(0);
        }

        unknownResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTypeAsync(unknownResponse)).Should().Be(ErrorBase + "folder-not-found");
    }

    // V9
    [Fact]
    public async Task Test_V9_DatabaseFailureReturnsGeneric500()
    {
        var cookie = await _h.LoginAsync();
        _h.Repository.BrowseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SELECT secret FROM dbo.file_index WHERE relative_path = 'D/Shares/Operations/Hidden' (Timeout expired)"));

        using var response = await _h.GetAsync("/files/browse?folder=Hidden", cookie);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProblemTypeAsync(response)).Should().Be(ErrorBase + "browse-failed");
        body.Should().NotContainEquivalentOf("SELECT");
        body.Should().NotContain("dbo.");
        body.Should().NotContain("D/Shares");
        body.Should().NotContain("Hidden");
        body.Should().NotContain("Timeout expired");
        _h.AuditEntries().Should().BeEmpty();
    }

    // SC-14
    [Fact]
    public async Task Test_SC14_ClientAbortCancelsBrowseAndNothingIsAudited()
    {
        var cookie = await _h.LoginAsync();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Repository.BrowseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(call => BlockUntilCancelledAsync(entered, call.Arg<CancellationToken>()));

        using var abort = new CancellationTokenSource();
        var request = _h.GetAsync("/files/browse", cookie, HttpCompletionOption.ResponseHeadersRead, abort.Token);

        var repositoryToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        repositoryToken.CanBeCanceled.Should().BeTrue("the request CancellationToken must reach the repository");
        repositoryToken.IsCancellationRequested.Should().BeFalse();

        abort.Cancel();

        // The aborted call ends either with the client-side cancellation or with a non-success
        // response from the cancelled request; it must never complete as a 200 listing.
        try
        {
            using var aborted = await request.WaitAsync(TimeSpan.FromSeconds(10));
            aborted.StatusCode.Should().NotBe(HttpStatusCode.OK);
        }
        catch (OperationCanceledException)
        {
        }

        SpinWait.SpinUntil(() => repositoryToken.IsCancellationRequested, TimeSpan.FromSeconds(10))
            .Should().BeTrue("aborting the HTTP request must cancel the database call");
        _h.AuditEntries().Should().BeEmpty("browsing is never audited");
    }

    private static async Task<FileBrowsePage> BlockUntilCancelledAsync(
        TaskCompletionSource<CancellationToken> entered, CancellationToken ct)
    {
        entered.TrySetResult(ct);
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }
}
