using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Components.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Web.Tests.FileViewer;

/// <summary>
/// CAS-040 §13 — SC-13 via bUnit, one theory case per §8.2 panel: clicking a file name opens
/// <see cref="FileViewerModal"/> with that row's <c>FileIndexId</c> (never its task-scoped
/// <c>FileId</c>) and the panel's <c>context</c>, without changing the row's checkbox selection.
/// The host screens are populated through their existing deep-link (<c>?taskId=</c>) and
/// quarantine-list load paths against <see cref="FakeFileViewerApiHandler"/>.
/// </summary>
public sealed class FileNameClickTests : TestContext
{
    private const int TaskId = 91;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeFileViewerApiHandler _apiHandler = new();

    public FileNameClickTests()
    {
        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = "cachet-archive-prod" })
            .Build());
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(new TaskPollingOptions { TaskManagerPollIntervalSeconds = 1 }));
        Services.AddLogging();
        Services.AddTransient<Cachet.ArchiveSync.Web.Http.FilesBrowseClient>(); // CAS-044: Files is the default mode
        Services.AddTransient<Cachet.ArchiveSync.Web.Http.SearchResultsBrowseClient>(); // CAS-048: results explorer
        Services.AddTransient<Cachet.ArchiveSync.Web.Http.QuarantineBrowseClient>(); // CAS-051: injected by FileExplorer

        var authContext = this.AddTestAuthorization();
        authContext.SetAuthorized("full-access-user");
        authContext.SetClaims(new Claim("b2_capability", "Full Access"));

        // The viewer's own metadata call — its content is not under test here.
        _apiHandler.OnGetFileMetadata = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(Json(new QuarantineListResponse { TotalCount = 0, Page = 1, PageSize = 25, Items = Array.Empty<QuarantineListItem>() }));
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private static PagedResult<T> Page<T>(params T[] items) => new() { Items = items, Page = 1, PageSize = 50, TotalCount = items.Length };

    private void CompletedTask(string type) =>
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(id == TaskId
            ? Json(new TaskDetailResponse
            {
                TaskId = TaskId,
                Type = type,
                SubmittedBy = "full-access-user",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-3),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Status = "Completed",
                IsAcknowledged = true,
                SearchParameters = type == "KeywordSearch" ? new TaskSearchParameters { Term = "invoice", Extension = "PDF" } : null,
            })
            : new HttpResponseMessage(HttpStatusCode.NotFound));

    private void NavigateToTask(string route)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(route);
        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameter("TaskId", TaskId));
    }

    /// <summary>Renders the host screen for <paramref name="panel"/> with one row whose name is
    /// <paramref name="name"/>, <c>FileId</c> = <paramref name="fileId"/> and
    /// <c>FileIndexId</c> = <paramref name="fileIndexId"/>.</summary>
    private IRenderedFragment RenderPanel(string panel, string name, int fileId, int fileIndexId)
    {
        switch (panel)
        {
            case "KS-Quarantine":
                {
                    _apiHandler.OnGetQuarantine = _ => Task.FromResult(Json(new QuarantineListResponse
                    {
                        TotalCount = 1,
                        Page = 1,
                        PageSize = 25,
                        Items = new[] { new QuarantineListItem { SourceTab = "Keyword Search", FileId = fileId, FileIndexId = fileIndexId, Name = name, Path = "/o-drive/" + name, QuarantinedOn = DateTimeOffset.UtcNow, Status = "Flagged" } },
                    }));
                    // The Quarantine panel renders only alongside displayed results, so the screen is opened on
                    // a Completed Keyword Search through its deep link (the same path the OD-* cases use).
                    CompletedTask("KeywordSearch");
                    _apiHandler.OnGetSearchResults = _ => Task.FromResult(Json(new SearchResultsResponse
                    {
                        TaskId = TaskId,
                        Status = "Completed",
                        FilenameMatches = Page<FilenameMatchItem>(),
                        ContentMatches = Page<ContentMatchItem>(),
                    }));
                    // Browse is not under test here; an empty page keeps the results explorer quiet.
                    _apiHandler.OnGetSearchBrowse = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    NavigateToTask("/");
                    var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();
                    cut.WaitForState(() => cut.Markup.Contains(name), LoadTimeout);
                    return cut;
                }

            case "OD-Removed":
            case "OD-New":
            case "OD-Modified":
                {
                    CompletedTask("ContentScan");
                    var removed = panel == "OD-Removed"
                        ? new[] { new RemovedFromODriveItem { FileId = fileId, FileIndexId = fileIndexId, Name = name, Path = "/b2/" + name, LastSeen = DateTimeOffset.UtcNow, SyncStatus = "Pending" } }
                        : Array.Empty<RemovedFromODriveItem>();
                    var added = panel == "OD-New"
                        ? new[] { new NewOnODriveItem { FileId = fileId, FileIndexId = fileIndexId, Name = name, Path = "/o-drive/" + name, Size = 10, ModifiedAt = DateTimeOffset.UtcNow, SyncStatus = "Pending" } }
                        : Array.Empty<NewOnODriveItem>();
                    var modified = panel == "OD-Modified"
                        ? new[] { new ModifiedOnODriveItem { FileId = fileId, FileIndexId = fileIndexId, Name = name, Path = "/o-drive/" + name, ModifiedAt = DateTimeOffset.UtcNow, Sha1Changed = true, SyncStatus = "Pending" } }
                        : Array.Empty<ModifiedOnODriveItem>();
                    _apiHandler.OnGetSyncResults = _ => Task.FromResult(Json(new SyncScanResultsResponse
                    {
                        TaskId = TaskId,
                        Status = "Completed",
                        RemovedFromODrive = Page(removed),
                        NewOnODrive = Page(added),
                        ModifiedOnODrive = Page(modified),
                    }));
                    NavigateToTask("/odrive-sync");
                    var cut = RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();
                    cut.WaitForState(() => cut.Markup.Contains(name), LoadTimeout);
                    return cut;
                }

            default: // QQ-Modal
                {
                    _apiHandler.OnGetQuarantine = _ => Task.FromResult(Json(new QuarantineListResponse
                    {
                        TotalCount = 1,
                        Page = 1,
                        PageSize = 25,
                        Items = new[] { new QuarantineListItem { SourceTab = "Keyword Search", FileId = fileId, FileIndexId = fileIndexId, Name = name, Path = "/o-drive/" + name, QuarantinedOn = DateTimeOffset.UtcNow, Status = "Flagged" } },
                    }));
                    var cut = RenderComponent<QuarantineQueueModal>();
                    cut.InvokeAsync(() => cut.Instance.OpenAsync()).GetAwaiter().GetResult();
                    cut.WaitForState(() => cut.Markup.Contains(name), LoadTimeout);
                    return cut;
                }
        }
    }

    private static IElement Row(IRenderedFragment cut, string name) =>
        cut.FindAll("tbody tr").Single(r => r.TextContent.Contains(name, StringComparison.Ordinal));

    // -----------------------------------------------------------------------------------------
    // SC-13
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("KS-Quarantine", "Quarantine", false)]
    [InlineData("OD-Removed", "ODriveSync", true)]
    [InlineData("OD-New", "ODriveSync", true)]
    [InlineData("OD-Modified", "ODriveSync", true)]
    [InlineData("QQ-Modal", "Quarantine", true)]
    public void Test_SC13_FileNameClickOpensViewerWithoutTogglingSelection(string panel, string expectedContext, bool hasCheckbox)
    {
        const int fileId = 12;          // task/entry-scoped result id
        const int fileIndexId = 500_777; // the file identity the viewer must receive (§17)
        var name = $"sc13-{panel.ToLowerInvariant()}.pdf";

        var cut = RenderPanel(panel, name, fileId, fileIndexId);

        // The name is a native <button> (keyboard-operable) with the §8.2 classes and title.
        var link = Row(cut, name).QuerySelector("button.cachet-file-link")!;
        link.Should().NotBeNull();
        link.ClassList.Should().Contain("cachet-cell-name");
        link.GetAttribute("type").Should().Be("button");
        link.GetAttribute("title").Should().Be($"Open {name}");
        link.TextContent.Should().Be(name);

        // Pre-select the row, so an accidental toggle on name-click would be visible.
        if (hasCheckbox)
        {
            Row(cut, name).QuerySelector("input[type=checkbox]")!.Change(true);
            Row(cut, name).QuerySelector("input[type=checkbox]")!.HasAttribute("checked").Should().BeTrue();
        }

        var viewersBefore = cut.FindComponents<FileViewerModal>();
        viewersBefore.Should().OnlyContain(v => v.Instance.FileIndexId == null, "no viewer is open before the click");

        Row(cut, name).QuerySelector("button.cachet-file-link")!.Click();

        var open = cut.FindComponents<FileViewerModal>().Where(v => v.Instance.FileIndexId != null).ToList();
        open.Should().ContainSingle("exactly one viewer opens");
        open[0].Instance.FileIndexId.Should().Be(fileIndexId, "the viewer is keyed on FileIndexId, never the task-scoped FileId");
        open[0].Instance.Context.Should().Be(expectedContext);

        if (hasCheckbox)
        {
            Row(cut, name).QuerySelector("input[type=checkbox]")!.HasAttribute("checked")
                .Should().BeTrue("a name click never toggles the row checkbox (§8.2)");
        }

        // The viewer asked Api for exactly that file with that context.
        cut.WaitForAssertion(() => _apiHandler.Requests.Should().Contain(r =>
            r.RequestUri!.PathAndQuery == $"/api/files/{fileIndexId}?context={expectedContext}"), LoadTimeout);

        // Closing returns to the host with the viewer closed and selection intact.
        cut.WaitForState(() => cut.FindAll(".cachet-fv-close-btn").Count > 0, LoadTimeout);
        cut.Find(".cachet-fv-close-btn").Click();
        cut.FindComponents<FileViewerModal>().Should().OnlyContain(v => v.Instance.FileIndexId == null);
        if (hasCheckbox)
        {
            Row(cut, name).QuerySelector("input[type=checkbox]")!.HasAttribute("checked").Should().BeTrue();
        }
    }

    /// <summary>
    /// CAS-048 §5a: replaces the former KS-Filename / KS-Content theory cases (those panels no longer
    /// exist). Clicking a file name in the Keyword Search results explorer opens the viewer with the
    /// file's <c>FileIndexId</c> and the <c>KeywordSearch</c> context (not <c>FileBrowser</c>), without
    /// toggling the row's selection checkbox.
    /// </summary>
    [Fact]
    public void Test_SC13_ResultsExplorerFileNameClickOpensViewerWithoutTogglingSelection()
    {
        const int fileIndexId = 201; // a1.pdf in the results fixture (Alpha, searchResultId 9001)
        CompletedTask("KeywordSearch");
        _apiHandler.OnGetSearchResults = _ => Task.FromResult(Json(new SearchResultsResponse
        {
            TaskId = TaskId,
            Status = "Completed",
            FilenameMatches = Page<FilenameMatchItem>(),
            ContentMatches = Page<ContentMatchItem>(),
        }));
        _apiHandler.OnGetSearchBrowse = (request, _) => FileExplorer.BrowseFixtures.ServeResults(request, TaskId);
        NavigateToTask("/");
        var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();
        cut.WaitForState(() => cut.FindAll("#fe-root tr.fe-row-folder").Count > 0, LoadTimeout);

        cut.FindAll("#fe-root tr.fe-row-folder").Single(r => r.QuerySelector(".fe-name")!.TextContent == "Alpha").Click();
        cut.WaitForAssertion(() => cut.Find("#fe-root .fe-crumb-current").TextContent.Should().Be("Alpha"), LoadTimeout);

        IElement FileRow() => cut.FindAll("#fe-root tr.fe-row-file").Single(r => r.QuerySelector(".fe-name")!.TextContent == "a1.pdf");

        FileRow().QuerySelector("input.fe-check")!.Change(true);
        FileRow().QuerySelector("input.fe-check")!.HasAttribute("checked").Should().BeTrue();
        cut.Find("#ks-send-to-quarantine").TextContent.Should().Contain("(1)");
        cut.FindComponents<FileViewerModal>().Should().OnlyContain(v => v.Instance.FileIndexId == null, "no viewer is open before the click");

        FileRow().Click();

        var open = cut.FindComponents<FileViewerModal>().Where(v => v.Instance.FileIndexId != null).ToList();
        open.Should().ContainSingle("exactly one viewer opens");
        open[0].Instance.FileIndexId.Should().Be(fileIndexId, "the viewer is keyed on FileIndexId, never the searchResultId");
        open[0].Instance.Context.Should().Be("KeywordSearch", "the audit trail stays tied to the search (CAS-048 §2.2)");
        FileRow().QuerySelector("input.fe-check")!.HasAttribute("checked").Should().BeTrue("a name click never toggles the selection checkbox");
        cut.Find("#ks-send-to-quarantine").TextContent.Should().Contain("(1)");

        cut.WaitForAssertion(() => _apiHandler.Requests.Should().Contain(r =>
            r.RequestUri!.PathAndQuery == $"/api/files/{fileIndexId}?context=KeywordSearch"), LoadTimeout);

        cut.WaitForState(() => cut.FindAll(".cachet-fv-close-btn").Count > 0, LoadTimeout);
        cut.Find(".cachet-fv-close-btn").Click();
        cut.FindComponents<FileViewerModal>().Should().OnlyContain(v => v.Instance.FileIndexId == null);
        FileRow().QuerySelector("input.fe-check")!.HasAttribute("checked").Should().BeTrue("the selection survives closing the viewer");
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
