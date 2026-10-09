using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Http;
using Cachet.ArchiveSync.Web.Tests.KeywordSearch;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Explorer = Cachet.ArchiveSync.Web.Components.Shared.FileExplorer;

namespace Cachet.ArchiveSync.Web.Tests.FileExplorer;

/// <summary>
/// CAS-044 §13 — bUnit tests for <c>FileExplorer.razor</c> and <c>FilesBrowseClient</c> (SC-4..SC-10,
/// SC-12, SC-15, V4..V8). The "CachetApi" client is redirected to
/// <see cref="FakeKeywordSearchApiHandler.OnGetFilesBrowse"/>; no real Api process is reached.
/// </summary>
public sealed class FileExplorerTests : TestContext
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeKeywordSearchApiHandler _api = new();

    public FileExplorerTests()
    {
        _api.OnGetFilesBrowse = BrowseFixtures.Serve;
        _api.OnGetSearchBrowse = BrowseFixtures.ServeResults;
        _api.OnGetSearchResultIds = BrowseFixtures.ServeResultIds;
        _api.OnGetQuarantine = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new QuarantineListResponse { TotalCount = 0, Page = 1, PageSize = 25, Items = Array.Empty<QuarantineListItem>() }),
        });

        var client = new HttpClient(_api) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(client));
        Services.AddTransient<FilesBrowseClient>();
        Services.AddTransient<SearchResultsBrowseClient>();
        Services.AddTransient<QuarantineBrowseClient>(); // CAS-051: injected by FileExplorer
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = "cachet-archive-prod" })
            .Build());
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(new TaskPollingOptions { TaskManagerPollIntervalSeconds = 1 }));
        var auth = this.AddTestAuthorization();
        // CAS-051 SC-9: Read Only keeps Files mode free of the quarantine state call/column so every
        // CAS-044/046/048 assertion here stays unchanged; results mode selection is driven by the
        // Selectable parameter, not the claim. Full Access Files-mode behavior: FileExplorerQuarantineTests.
        auth.SetAuthorized("read-only-user");
        auth.SetClaims(new System.Security.Claims.Claim("b2_capability", "Read Only"));
    }

    private IRenderedComponent<Explorer> RenderExplorer(Action<ComponentParameterCollectionBuilder<Explorer>>? parameters = null)
    {
        var cut = parameters is null ? RenderComponent<Explorer>() : RenderComponent(parameters);
        cut.WaitForState(() => cut.FindAll("tr.fe-row").Count > 0 || cut.FindAll(".fe-error, .fe-session-expired, .fe-empty").Count > 0, Wait);
        return cut;
    }

    private static List<string> RowNames(IRenderedComponent<Explorer> cut) =>
        cut.FindAll("tr.fe-row .fe-name").Select(n => n.TextContent).ToList();

    private static IElement FolderRow(IRenderedComponent<Explorer> cut, string name) =>
        cut.FindAll("tr.fe-row-folder").Single(r => r.QuerySelector(".fe-name")!.TextContent == name);

    private static IElement FileRow(IRenderedComponent<Explorer> cut, string name) =>
        cut.FindAll("tr.fe-row-file").Single(r => r.QuerySelector(".fe-name")!.TextContent == name);

    private List<(string folder, bool foldersOnly)> BrowseCalls() =>
        _api.Requests.ToList()
            .Where(r => r.RequestUri!.AbsolutePath.EndsWith("/files/browse", StringComparison.Ordinal))
            .Select(BrowseFixtures.Query)
            .ToList();

    private static void WaitForCurrent(IRenderedComponent<Explorer> cut, string name) =>
        cut.WaitForAssertion(() => cut.Find(".fe-crumb-current").TextContent.Should().Be(name), Wait);

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC4_ExplorerLoadsRootFoldersBeforeFiles()
    {
        var cut = RenderExplorer();

        // First (and only) call is the root listing with no query.
        _api.Requests.Select(r => r.RequestUri!.PathAndQuery).Should().Equal("/api/files/browse");

        // Folders first, then files.
        RowNames(cut).Should().Equal("Invoices", "Reports", "readme.txt");
        cut.FindAll("tr.fe-row")[0].ClassList.Should().Contain("fe-row-folder");
        cut.FindAll("tr.fe-row")[2].ClassList.Should().Contain("fe-row-file");

        // Tree shows the root, expanded, with its two folders; list columns are the Windows set.
        cut.Find("ul[role=tree]").Should().NotBeNull();
        cut.Find("li[role=treeitem]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".fe-node-name").Select(n => n.TextContent).Should().Equal("Archive", "Invoices", "Reports");
        cut.FindAll("table.fe-table th").Select(t => t.TextContent.Trim()).Should().Equal("Name", "Date modified", "Type", "Size");
        cut.Find(".fe-crumb-current").TextContent.Should().Be("Archive");
        cut.Find(".fe-status-count").TextContent.Should().Be("3 items");
        cut.Find(".fe-status-source").TextContent.Should().Contain("metadata only");

        // File presentation: friendly type name and size.
        FileRow(cut, "readme.txt").QuerySelectorAll("td").Select(t => t.TextContent).Should().Contain("Text Document").And.Contain("300 B");
        FolderRow(cut, "Invoices").QuerySelectorAll("td")[2].TextContent.Should().Be("File folder");
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_ClickingListFolderOpensItAndUpdatesBreadcrumb()
    {
        var cut = RenderExplorer();

        FolderRow(cut, "Invoices").Click();

        WaitForCurrent(cut, "Invoices");
        BrowseCalls().Should().Equal((string.Empty, false), ("Invoices", false));
        RowNames(cut).Should().Equal("2025", "alpha-invoice.pdf", "beta-ledger.xlsx");
        cut.Find(".fe-crumb-link").TextContent.Should().Be("Archive");
        cut.Find(".fe-node-row.is-current .fe-node-name").TextContent.Should().Be("Invoices");
        cut.Find("#fe-up").HasAttribute("disabled").Should().BeFalse();
        FileRow(cut, "beta-ledger.xlsx").QuerySelectorAll("td").Select(t => t.TextContent).Should().Contain("5.0 MB").And.Contain("Excel Workbook");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-046 SC-5 / SC-6 (chevron only on folders with subfolders; tolerant default)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_TreeChevronOnlyOnFoldersWithSubfolders()
    {
        _api.OnGetFilesBrowse = request =>
        {
            var (folder, _) = BrowseFixtures.Query(request);
            FolderBrowseResponse body = folder == "Branch"
                ? BrowseFixtures.Listing("Branch", Array.Empty<BrowseFolderItem>())   // flagged true but nothing underneath
                : BrowseFixtures.Listing(string.Empty, new[]
                {
                    BrowseFixtures.Folder("Leaf", hasSubfolders: false),
                    BrowseFixtures.Folder("Branch", hasSubfolders: true),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        };

        var cut = RenderExplorer();

        cut.FindAll("button.fe-chevron[aria-label='Expand Leaf']").Should().BeEmpty("a leaf folder has no chevron");
        cut.FindAll("button.fe-chevron[aria-label='Expand Branch']").Should().ContainSingle();
        cut.FindAll(".fe-node-name").Select(n => n.TextContent).Should().Contain("Leaf");

        // A node whose children were loaded and are empty loses its chevron.
        cut.Find("button[aria-label='Expand Branch']").Click();
        cut.WaitForAssertion(() => cut.FindAll("button.fe-chevron[aria-label$=' Branch']").Should().BeEmpty(), Wait);
        BrowseCalls().Should().Contain(("Branch", true));
        cut.FindAll("button.fe-chevron[aria-label='Expand Leaf']").Should().BeEmpty();
    }

    [Fact]
    public async Task Test_SC6_MissingHasSubfoldersFieldKeepsChevron()
    {
        const string legacyJson = "{\"rootName\":\"Archive\",\"folder\":\"\",\"parent\":null,\"breadcrumb\":[],"
            + "\"folders\":[{\"name\":\"Old\",\"path\":\"Old\",\"latestModifiedAt\":\"2025-03-14T15:30:00Z\"}],"
            + "\"files\":[],\"totalFolders\":1,\"totalFiles\":0}";

        // Deserialisation exactly as FilesBrowseClient does it (ReadFromJsonAsync, web defaults).
        using var content = new StringContent(legacyJson, System.Text.Encoding.UTF8, "application/json");
        var parsed = await content.ReadFromJsonAsync<FolderBrowseResponse>();
        parsed!.Folders.Should().ContainSingle().Which.HasSubfolders.Should().BeTrue();

        // And through the rendered tree.
        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(legacyJson, System.Text.Encoding.UTF8, "application/json"),
        });

        var cut = RenderExplorer();

        cut.FindAll("button.fe-chevron[aria-label='Expand Old']").Should().ContainSingle();
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_TreeExpandLoadsFoldersOnlyOnce()
    {
        var cut = RenderExplorer();

        cut.Find("button[aria-label='Expand Reports']").Click();
        cut.WaitForAssertion(() => cut.FindAll(".fe-node-name").Select(n => n.TextContent).Should().Contain("Q1"), Wait);

        // Collapse then expand again: served from the node cache, no second call.
        cut.Find("button[aria-label='Collapse Reports']").Click();
        cut.FindAll(".fe-node-name").Select(n => n.TextContent).Should().NotContain("Q1");
        cut.Find("button[aria-label='Expand Reports']").Click();
        cut.FindAll(".fe-node-name").Select(n => n.TextContent).Should().Contain("Q1");

        BrowseCalls().Where(c => c.foldersOnly).Should().Equal(new[] { ("Reports", true) });
        // The list pane is untouched by a tree expand.
        RowNames(cut).Should().Equal("Invoices", "Reports", "readme.txt");
        cut.Find(".fe-crumb-current").TextContent.Should().Be("Archive");
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_UpAndBreadcrumbNavigate()
    {
        var cut = RenderExplorer();
        cut.Find("#fe-up").HasAttribute("disabled").Should().BeTrue("the root has no parent");

        FolderRow(cut, "Invoices").Click();
        WaitForCurrent(cut, "Invoices");
        FolderRow(cut, "2025").Click();
        WaitForCurrent(cut, "2025");
        cut.FindAll(".fe-crumb-link").Select(c => c.TextContent).Should().Equal("Archive", "Invoices");

        cut.Find("#fe-up").Click();
        WaitForCurrent(cut, "Invoices");

        cut.Find("#fe-up").Click();
        WaitForCurrent(cut, "Archive");
        cut.Find("#fe-up").HasAttribute("disabled").Should().BeTrue();

        FolderRow(cut, "Invoices").Click();
        WaitForCurrent(cut, "Invoices");
        FolderRow(cut, "2025").Click();
        WaitForCurrent(cut, "2025");
        cut.FindAll(".fe-crumb-link").Single(c => c.TextContent == "Invoices").Click();
        WaitForCurrent(cut, "Invoices");
        cut.FindAll(".fe-crumb-link").Single(c => c.TextContent == "Archive").Click();
        WaitForCurrent(cut, "Archive");

        BrowseCalls().Select(c => c.folder).Should().Contain("Invoices");
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_ClickingFileOpensViewerWithFileBrowserContext()
    {
        var page = RenderComponent<Web.Components.Pages.KeywordSearch>();
        page.WaitForState(() => page.FindAll("tr.fe-row-file").Count > 0, Wait);

        page.FindAll("tr.fe-row-file").Single().Click();

        var viewer = page.FindComponent<Web.Components.Shared.FileViewerModal>();
        viewer.Instance.FileIndexId.Should().Be(101);
        viewer.Instance.Context.Should().Be("FileBrowser");
        page.WaitForAssertion(() => _api.Requests.ToList().Should().Contain(r =>
            r.RequestUri!.PathAndQuery == "/api/files/101?context=FileBrowser"), Wait);
        page.Find("tr.fe-row-file").ClassList.Should().Contain("is-selected");
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC9_FilterNarrowsLoadedItemsLocally()
    {
        var cut = RenderExplorer();
        var callsBefore = _api.Requests.Count;

        cut.Find("#fe-filter").Input("INV");

        RowNames(cut).Should().Equal("Invoices");
        cut.Find(".fe-status-count").TextContent.Should().Be("1 item");

        cut.Find("#fe-filter").Input("zzz");
        cut.Find(".fe-empty").TextContent.Should().Be("No items match this filter.");

        cut.Find("#fe-filter").Input(string.Empty);
        RowNames(cut).Should().Equal("Invoices", "Reports", "readme.txt");

        _api.Requests.Count.Should().Be(callsBefore, "filtering is client-side only and never calls the Api");

        // Opening another folder clears the filter.
        cut.Find("#fe-filter").Input("rep");
        FolderRow(cut, "Reports").Click();
        WaitForCurrent(cut, "Reports");
        cut.Find("#fe-filter").GetAttribute("value").Should().BeNullOrEmpty();
        BrowseCalls().Should().Equal((string.Empty, false), ("Reports", false));
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC10_LargeFolderShowsEveryItemThroughVirtualizedList()
    {
        const int total = 2500;
        var files = Enumerable.Range(1, total).Select(i => BrowseFixtures.File(10_000 + i, $"file-{i:D4}.pdf")).ToList();
        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(BrowseFixtures.Listing(string.Empty, null, files)),
        });

        var cut = RenderExplorer();

        cut.Find(".fe-status-count").TextContent.Should().Be("2500 items", "every item is in the list; there is no 'Load more' paging");
        cut.Markup.Should().NotContainEquivalentOf("load more");
        cut.FindAll("button").Select(b => b.TextContent).Should().NotContain(t => t.Contains("more", StringComparison.OrdinalIgnoreCase));
        cut.FindAll("tr.fe-row").Count.Should().BeLessThan(total / 2, "Virtualize renders only the rows near the viewport");
        _api.Requests.Count.Should().Be(1, "the whole folder came from a single call");
    }

    // -----------------------------------------------------------------------------------------
    // SC-12
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC12_NoServerPathOrKeyInMarkupOrRequests()
    {
        var cut = RenderExplorer();
        FolderRow(cut, "Invoices").Click();
        WaitForCurrent(cut, "Invoices");
        cut.Find("button[aria-label='Expand Reports']").Click();
        cut.WaitForAssertion(() => cut.FindAll(".fe-node-name").Select(n => n.TextContent).Should().Contain("Q1"), Wait);

        // Markup: only response-supplied names; no mirror/server path, key or credential vocabulary.
        foreach (var forbidden in new[] { "applicationKey", "keyId", "secret", "password", "token", "C:\\", "/mnt/", "/srv/", "o-drive" })
        {
            cut.Markup.Should().NotContainEquivalentOf(forbidden);
        }

        // Folder links are buttons, never an href built from a name.
        cut.FindAll("a[href]").Should().BeEmpty();

        // Requests: relative, to files/browse only, with no credential headers.
        var requests = _api.Requests.ToList();
        requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/files/browse");
        requests.Should().OnlyContain(r => r.Headers.Authorization == null && !r.Headers.Contains("X-B2-Application-Key"));
        requests.Should().OnlyContain(r => r.Content == null);
    }

    // -----------------------------------------------------------------------------------------
    // SC-15
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC15_SpinnerShownWhileLoadingAndLatestFolderWins()
    {
        var cut = RenderExplorer();
        cut.FindAll("#fe-list-spinner").Should().BeEmpty("no spinner once the root has loaded");

        var gates = new Dictionary<string, TaskCompletionSource<FolderBrowseResponse>>
        {
            ["Invoices"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Reports"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        _api.OnGetFilesBrowse = async request =>
        {
            var (folder, _) = BrowseFixtures.Query(request);
            var body = await gates[folder].Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        };

        FolderRow(cut, "Invoices").Click();
        cut.Find("#fe-list-spinner").GetAttribute("role").Should().Be("status");
        cut.Find("#fe-list-spinner").TextContent.Should().Contain("Loading");
        cut.Find(".fe-crumb-current").TextContent.Should().Be("Archive", "the previous listing stays until the call returns");

        // Superseded by a second folder click before the first returns.
        FolderRow(cut, "Reports").Click();
        gates["Reports"].SetResult(BrowseFixtures.Reports());
        WaitForCurrent(cut, "Reports");
        cut.FindAll("#fe-list-spinner").Should().BeEmpty();

        // The late first response must not overwrite the latest folder.
        gates["Invoices"].SetResult(BrowseFixtures.Invoices());
        Thread.Sleep(200);
        cut.Find(".fe-crumb-current").TextContent.Should().Be("Reports");
        RowNames(cut).Should().Equal("Q1", "summary.docx");
        cut.FindAll("#fe-list-spinner").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // V4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V4_BrowseUnauthorizedShowsSessionExpired()
    {
        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var cut = RenderExplorer();

        var banner = cut.Find(".fe-session-expired");
        banner.TextContent.Should().Contain("Your session has expired — sign in again to see results.");
        banner.QuerySelector("a")!.GetAttribute("href").Should().Be("login");
        cut.FindAll("tr.fe-row").Should().BeEmpty();
        cut.FindAll(".fe-error").Should().BeEmpty();
        _api.Requests.Count.Should().Be(1, "no retry loop after a 401");
    }

    // -----------------------------------------------------------------------------------------
    // V5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V5_FolderNotFoundShowsMessageAndReturnsToRoot()
    {
        var cut = RenderExplorer();
        FolderRow(cut, "Invoices").Click();
        WaitForCurrent(cut, "Invoices");

        // Invoices/2025 has been removed from the archive since the listing was fetched.
        _api.OnGetFilesBrowse = request => BrowseFixtures.Query(request).folder == "Invoices/2025"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : BrowseFixtures.Serve(request);
        FolderRow(cut, "2025").Click();

        cut.WaitForAssertion(() => cut.Find(".fe-crumb-current").TextContent.Should().Be("Archive"), Wait);
        cut.Find(".fe-folder-gone").TextContent.Should().Be("This folder is no longer in the archive.");
        RowNames(cut).Should().Equal("Invoices", "Reports", "readme.txt");
        cut.FindAll(".fe-error").Should().BeEmpty();

        // The notice clears on the next navigation.
        FolderRow(cut, "Reports").Click();
        WaitForCurrent(cut, "Reports");
        cut.FindAll(".fe-folder-gone").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // V6
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("500")]
    [InlineData("throws")]
    [InlineData("badbody")]
    public void Test_V6_BrowseFailureShowsGenericMessageWithRetry(string failure)
    {
        _api.OnGetFilesBrowse = failure switch
        {
            "500" => _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"detail\":\"SQL timeout at C:\\\\secret\\\\path\"}"),
            }),
            "throws" => _ => throw new HttpRequestException("connection refused to http://internal-host/api"),
            _ => _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }),
        };

        var cut = RenderExplorer();

        var error = cut.Find(".fe-error");
        error.GetAttribute("role").Should().Be("alert");
        error.TextContent.Should().Contain("The folder list could not be loaded. Try again.");
        cut.Markup.Should().NotContain("SQL timeout").And.NotContain("internal-host").And.NotContain("secret");
        cut.FindAll("#fe-list-spinner").Should().BeEmpty();

        // Retry re-issues the same (root) request and recovers.
        _api.OnGetFilesBrowse = BrowseFixtures.Serve;
        cut.Find("#fe-retry").Click();
        cut.WaitForAssertion(() => RowNames(cut).Should().Equal("Invoices", "Reports", "readme.txt"), Wait);
        cut.FindAll(".fe-error").Should().BeEmpty();
    }

    [Fact]
    public void Test_V6_BrowseFailureShowsGenericMessageWithRetry_TreeExpandFailureKeepsListUsable()
    {
        var cut = RenderExplorer();
        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        cut.Find("button[aria-label='Expand Reports']").Click();

        cut.WaitForAssertion(() => cut.Find(".fe-tree-error").TextContent.Should().Contain("could not be loaded"), Wait);
        RowNames(cut).Should().Equal("Invoices", "Reports", "readme.txt");
    }

    // -----------------------------------------------------------------------------------------
    // V7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V7_FileOpenUsesFileIndexIdOnly()
    {
        int? opened = null;
        var cut = RenderExplorer(p => p.Add(c => c.OnFileOpened, (int id) => opened = id));

        var row = FileRow(cut, "readme.txt");
        row.GetAttribute("data-file-index-id").Should().Be("101");
        row.Click();

        opened.Should().Be(101);
        // Opening a file makes no Api call of its own (the host's viewer does) and never touches a path.
        _api.Requests.Count.Should().Be(1);

        // Keyboard: Enter opens, other keys do not.
        opened = null;
        FileRow(cut, "readme.txt").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "a" });
        opened.Should().BeNull();
        FileRow(cut, "readme.txt").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        opened.Should().Be(101);
    }

    // -----------------------------------------------------------------------------------------
    // V8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V8_BrowseFolderParamComesFromResponsesOnly()
    {
        var cut = RenderExplorer();
        var supplied = new HashSet<string>(StringComparer.Ordinal) { string.Empty };

        void Learn(FolderBrowseResponse r)
        {
            supplied.Add(r.Folder);
            foreach (var f in r.Folders)
            {
                supplied.Add(f.Path);
            }

            foreach (var b in r.Breadcrumb)
            {
                supplied.Add(b.Path);
            }

            if (r.Parent is not null)
            {
                supplied.Add(r.Parent);
            }
        }

        Learn(BrowseFixtures.Root());
        Learn(BrowseFixtures.Invoices());
        Learn(BrowseFixtures.Invoices2025());
        Learn(BrowseFixtures.Reports());

        cut.Find("#fe-filter").Input("Inv"); // user-typed text must never reach the Api
        FolderRow(cut, "Invoices").Click();
        WaitForCurrent(cut, "Invoices");
        cut.Find("#fe-filter").Input("202"); // user-typed text, still matches the row
        FolderRow(cut, "2025").Click();
        WaitForCurrent(cut, "2025");
        cut.Find("#fe-up").Click();
        WaitForCurrent(cut, "Invoices");
        cut.Find("button[aria-label='Expand Reports']").Click();
        cut.WaitForAssertion(() => BrowseCalls().Should().Contain(("Reports", true)), Wait);

        BrowseCalls().Should().OnlyContain(c => supplied.Contains(c.folder));
        _api.Requests.ToList().Select(r => r.RequestUri!.Query)
            .Should().NotContain(q => q.Contains("202&") || q.EndsWith("=202") || q.EndsWith("=Inv"));
    }

    // -----------------------------------------------------------------------------------------
    // FilesBrowseClient: request shapes (folder value escaped) and outcome mapping (V4-V6 plumbing).
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V8_BrowseFolderParamComesFromResponsesOnly_ClientEscapesFolderAndMapsOutcomes()
    {
        var client = Services.GetRequiredService<FilesBrowseClient>();
        var seen = new List<string>();
        _api.OnGetFilesBrowse = request =>
        {
            seen.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(BrowseFixtures.Root()),
            });
        };

        (await client.BrowseAsync(null, false, CancellationToken.None)).Outcome.Should().Be(FilesBrowseOutcome.Success);
        await client.BrowseAsync("A B/C&D", true, CancellationToken.None);
        await client.BrowseAsync("Invoices", false, CancellationToken.None);
        await client.BrowseAsync(null, true, CancellationToken.None);

        seen.Should().Equal(
            "/api/files/browse",
            "/api/files/browse?folder=A%20B%2FC%26D&foldersOnly=true",
            "/api/files/browse?folder=Invoices",
            "/api/files/browse?foldersOnly=true");

        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        (await client.BrowseAsync("x", false, CancellationToken.None)).Outcome.Should().Be(FilesBrowseOutcome.NotFound);
        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        (await client.BrowseAsync("x", false, CancellationToken.None)).Outcome.Should().Be(FilesBrowseOutcome.Unauthorized);
        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") });
        (await client.BrowseAsync("x", false, CancellationToken.None)).Outcome.Should().Be(FilesBrowseOutcome.Failed);
        _api.OnGetFilesBrowse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        (await client.BrowseAsync("x", false, CancellationToken.None)).Outcome.Should().Be(FilesBrowseOutcome.Failed);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => client.BrowseAsync("x", false, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // =========================================================================================
    // CAS-048 — results mode: the same explorer over GET search/{taskId}/browse (CAS-049), with a
    // checkbox column for files and folders.
    // =========================================================================================

    private const int ResultsTask = 7;

    private IRenderedComponent<Explorer> RenderResults(
        Action<ComponentParameterCollectionBuilder<Explorer>>? more = null,
        HashSet<int>? selected = null,
        List<Explorer.SelectionChange>? changes = null)
    {
        selected ??= new HashSet<int>();
        changes ??= new List<Explorer.SelectionChange>();
        var cut = RenderComponent<Explorer>(p =>
        {
            p.Add(c => c.ResultsTaskId, ResultsTask)
             .Add(c => c.Selectable, true)
             .Add(c => c.SelectedIds, selected)
             .Add(c => c.OnSelectionChanged, (Explorer.SelectionChange change) =>
             {
                 changes.Add(change);
                 foreach (var id in change.Ids)
                 {
                     if (change.Selected)
                     {
                         selected.Add(id);
                     }
                     else
                     {
                         selected.Remove(id);
                     }
                 }
             });
            more?.Invoke(p);
        });
        cut.WaitForState(() => cut.FindAll("tr.fe-row").Count > 0 || cut.FindAll(".fe-error, .fe-session-expired, .fe-empty").Count > 0, Wait);
        return cut;
    }

    private List<(string folder, bool foldersOnly)> ResultsBrowseCalls() =>
        _api.Requests.ToList()
            .Where(r => r.RequestUri!.AbsolutePath == $"/api/search/{ResultsTask}/browse")
            .Select(BrowseFixtures.Query)
            .ToList();

    private List<string> ResultIdsCalls() =>
        _api.Requests.ToList()
            .Where(r => r.RequestUri!.AbsolutePath == $"/api/search/{ResultsTask}/browse/result-ids")
            .Select(r => BrowseFixtures.Query(r).folder)
            .ToList();

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_ResultFolderDrillDown()
    {
        var cut = RenderResults(p => p.Add(c => c.TotalMatches, 3));

        // Root: folders that lead to matches, read through the results browse endpoint (not files/browse).
        RowNames(cut).Should().Equal("Alpha", "Beta");
        ResultsBrowseCalls().Should().Equal((string.Empty, false));
        _api.Requests.ToList().Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/files/browse"));
        cut.FindAll(".fe-node-name").Select(n => n.TextContent).Should().Equal("Archive", "Alpha", "Beta");
        cut.Find(".fe-status-count").TextContent.Should().Be("2 items in this folder · 3 matches in total");
        cut.Find("#fe-up").HasAttribute("disabled").Should().BeTrue();

        // Opening a result folder calls browse for that folder and lists its subfolders and matched files.
        FolderRow(cut, "Alpha").Click();
        WaitForCurrent(cut, "Alpha");
        ResultsBrowseCalls().Should().Equal((string.Empty, false), ("Alpha", false));
        RowNames(cut).Should().Equal("Sub", "a1.pdf");
        cut.Find(".fe-crumb-link").TextContent.Should().Be("Archive");
        cut.Find(".fe-node-row.is-current .fe-node-name").TextContent.Should().Be("Alpha");
        cut.Find("#fe-up").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".fe-status-count").TextContent.Should().Be("2 items in this folder · 3 matches in total");

        // Deeper, then Up and the breadcrumb work as in Files mode.
        FolderRow(cut, "Sub").Click();
        WaitForCurrent(cut, "Sub");
        RowNames(cut).Should().Equal("a2.pdf");
        cut.FindAll(".fe-crumb-link").Select(c => c.TextContent).Should().Equal("Archive", "Alpha");
        cut.Find("#fe-up").Click();
        WaitForCurrent(cut, "Alpha");
        cut.FindAll(".fe-crumb-link").Single(c => c.TextContent == "Archive").Click();
        WaitForCurrent(cut, "Archive");
        cut.Find("#fe-up").HasAttribute("disabled").Should().BeTrue();

        // The tree highlight follows a tree click too.
        cut.FindAll(".fe-node-name").Single(n => n.TextContent == "Beta").Click();
        WaitForCurrent(cut, "Beta");
        cut.Find(".fe-node-row.is-current .fe-node-name").TextContent.Should().Be("Beta");
        RowNames(cut).Should().Equal("b1.pdf");

        // Empty-root copy and the omitted total.
        _api.OnGetSearchBrowse = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(BrowseFixtures.Listing(string.Empty)),
        });
        var empty = RenderResults(p => p.Add(c => c.ResultsEmptyMessage, "No files in that date range."));
        empty.Find(".fe-empty").TextContent.Should().Be("No files in that date range.");
        empty.Find(".fe-status-count").TextContent.Should().Be("0 items in this folder", "no total is shown when it is unknown");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 V7
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("401")]
    [InlineData("404")]
    [InlineData("409")]
    [InlineData("500")]
    [InlineData("throws")]
    public void Test_V7_ResultsBrowseErrorStates(string failure)
    {
        _api.OnGetSearchBrowse = (_, _) => failure switch
        {
            "401" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            "404" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"task at C:\\\\secret\\\\path\"}", System.Text.Encoding.UTF8, "application/json") }),
            "409" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"detail\":\"task-not-complete internal-host\"}", System.Text.Encoding.UTF8, "application/json") }),
            "500" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"detail\":\"SQL timeout\"}", System.Text.Encoding.UTF8, "application/json") }),
            _ => throw new HttpRequestException("connection refused to http://internal-host/api"),
        };

        var cut = RenderResults();

        if (failure == "401")
        {
            var banner = cut.Find(".fe-session-expired");
            banner.TextContent.Should().Contain("Your session has expired — sign in again to see results.");
            banner.QuerySelector("a")!.GetAttribute("href").Should().Be("login");
            cut.FindAll(".fe-error").Should().BeEmpty();
            ResultsBrowseCalls().Should().HaveCount(1, "no retry loop after a 401");
            return;
        }

        var error = cut.Find(".fe-error");
        error.GetAttribute("role").Should().Be("alert");
        error.TextContent.Should().Contain("The folder list could not be loaded. Try again.");
        cut.Markup.Should().NotContain("secret").And.NotContain("internal-host").And.NotContain("SQL timeout").And.NotContain("task-not-complete");

        // Retry re-issues the same (root) request and recovers.
        _api.OnGetSearchBrowse = BrowseFixtures.ServeResults;
        cut.Find("#fe-retry").Click();
        cut.WaitForAssertion(() => RowNames(cut).Should().Equal("Alpha", "Beta"), Wait);
        cut.FindAll(".fe-error").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 V8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V8_ResultsFolderParamFromResponsesOnly()
    {
        var cut = RenderResults();
        var supplied = new HashSet<string>(StringComparer.Ordinal) { string.Empty };

        void Learn(FolderBrowseResponse r)
        {
            supplied.Add(r.Folder);
            foreach (var f in r.Folders)
            {
                supplied.Add(f.Path);
            }

            foreach (var b in r.Breadcrumb)
            {
                supplied.Add(b.Path);
            }

            if (r.Parent is not null)
            {
                supplied.Add(r.Parent);
            }
        }

        Learn(BrowseFixtures.ResultsRoot());
        Learn(BrowseFixtures.ResultsAlpha());
        Learn(BrowseFixtures.ResultsAlphaSub());
        Learn(BrowseFixtures.ResultsBeta());

        cut.Find("#fe-filter").Input("Alp"); // user-typed text must never reach the Api
        FolderRow(cut, "Alpha").Click();
        WaitForCurrent(cut, "Alpha");
        cut.Find("#fe-filter").Input("Su"); // user-typed text, still matches the row
        FolderRow(cut, "Sub").Click();
        WaitForCurrent(cut, "Sub");
        cut.Find("#fe-up").Click();
        WaitForCurrent(cut, "Alpha");

        ResultsBrowseCalls().Should().OnlyContain(c => supplied.Contains(c.folder));
        ResultsBrowseCalls().Select(c => c.folder).Should().Equal(string.Empty, "Alpha", "Alpha/Sub", "Alpha");
        _api.Requests.ToList().Select(r => r.RequestUri!.Query)
            .Should().NotContain(q => q.Contains("Alp") && !q.Contains("folder=Alpha") || q.Contains("=Su") || q.EndsWith("=Alp"));
    }

    [Fact]
    public async Task Test_V8_ResultsFolderParamFromResponsesOnly_ClientEscapesFolderAndMapsOutcomes()
    {
        var client = Services.GetRequiredService<SearchResultsBrowseClient>();
        var seen = new List<string>();
        _api.OnGetSearchBrowse = (request, _) =>
        {
            seen.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(BrowseFixtures.ResultsRoot()) });
        };

        (await client.BrowseAsync(5, null, false, CancellationToken.None)).Outcome.Should().Be(SearchResultsBrowseOutcome.Success);
        await client.BrowseAsync(5, "A B/C&D", true, CancellationToken.None);
        await client.BrowseAsync(5, "Alpha", false, CancellationToken.None);
        await client.BrowseAsync(5, null, true, CancellationToken.None);

        seen.Should().Equal(
            "/api/search/5/browse",
            "/api/search/5/browse?folder=A%20B%2FC%26D&foldersOnly=true",
            "/api/search/5/browse?folder=Alpha",
            "/api/search/5/browse?foldersOnly=true");

        foreach (var (status, expected) in new[]
        {
            (HttpStatusCode.NotFound, SearchResultsBrowseOutcome.NotFound),
            (HttpStatusCode.Unauthorized, SearchResultsBrowseOutcome.Unauthorized),
            (HttpStatusCode.Conflict, SearchResultsBrowseOutcome.Conflict),
            (HttpStatusCode.ServiceUnavailable, SearchResultsBrowseOutcome.Failed),
            (HttpStatusCode.BadRequest, SearchResultsBrowseOutcome.Failed),
        })
        {
            _api.OnGetSearchBrowse = (_, _) => Task.FromResult(new HttpResponseMessage(status));
            (await client.BrowseAsync(5, "x", false, CancellationToken.None)).Outcome.Should().Be(expected, status.ToString());
        }

        _api.OnGetSearchBrowse = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") });
        (await client.BrowseAsync(5, "x", false, CancellationToken.None)).Outcome.Should().Be(SearchResultsBrowseOutcome.Failed);
        _api.OnGetSearchBrowse = (_, _) => throw new HttpRequestException("down");
        (await client.BrowseAsync(5, "x", false, CancellationToken.None)).Outcome.Should().Be(SearchResultsBrowseOutcome.Failed);
        _api.OnGetSearchBrowse = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json") });
        (await client.BrowseAsync(5, "x", false, CancellationToken.None)).Outcome.Should().Be(SearchResultsBrowseOutcome.Failed);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => client.BrowseAsync(5, "x", false, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Test_V9_FolderSelectionResolvedByApiOnly_ClientEscapesFolderAndMapsOutcomes()
    {
        var client = Services.GetRequiredService<SearchResultsBrowseClient>();
        var seen = new List<string>();
        _api.OnGetSearchResultIds = (request, _) =>
        {
            seen.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SearchResultIdsResponse { SearchResultIds = new[] { 1, 2 }, Count = 2 }),
            });
        };

        var ok = await client.GetResultIdsAsync(5, "A B/C&D", CancellationToken.None);
        ok.Outcome.Should().Be(SearchResultsBrowseOutcome.Success);
        ok.Ids.Should().Equal(1, 2);
        await client.GetResultIdsAsync(5, null, CancellationToken.None);
        seen.Should().Equal("/api/search/5/browse/result-ids?folder=A%20B%2FC%26D", "/api/search/5/browse/result-ids");

        var problem = new StringContent("{\"type\":\"https://cachet.tracecounts.internal/errors/selection-too-large\",\"status\":400}", System.Text.Encoding.UTF8, "application/json");
        foreach (var (response, expected) in new (Func<HttpResponseMessage>, SearchResultsBrowseOutcome)[]
        {
            (() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = problem }, SearchResultsBrowseOutcome.SelectionTooLarge),
            (() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"type\":\"https://x/other\"}", System.Text.Encoding.UTF8, "application/json") }, SearchResultsBrowseOutcome.Failed),
            (() => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("not json") }, SearchResultsBrowseOutcome.Failed),
            (() => new HttpResponseMessage(HttpStatusCode.NotFound), SearchResultsBrowseOutcome.NotFound),
            (() => new HttpResponseMessage(HttpStatusCode.Unauthorized), SearchResultsBrowseOutcome.Unauthorized),
            (() => new HttpResponseMessage(HttpStatusCode.Conflict), SearchResultsBrowseOutcome.Conflict),
            (() => new HttpResponseMessage(HttpStatusCode.InternalServerError), SearchResultsBrowseOutcome.Failed),
            (() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") }, SearchResultsBrowseOutcome.Failed),
        })
        {
            _api.OnGetSearchResultIds = (_, _) => Task.FromResult(response());
            (await client.GetResultIdsAsync(5, "x", CancellationToken.None)).Outcome.Should().Be(expected);
        }

        _api.OnGetSearchResultIds = (_, _) => throw new HttpRequestException("down");
        (await client.GetResultIdsAsync(5, "x", CancellationToken.None)).Outcome.Should().Be(SearchResultsBrowseOutcome.Failed);
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 V9
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ok")]
    [InlineData("tooLarge")]
    [InlineData("serverError")]
    [InlineData("unauthorized")]
    [InlineData("totalCap")]
    public void Test_V9_FolderSelectionResolvedByApiOnly(string scenario)
    {
        var selected = new HashSet<int>();
        var changes = new List<Explorer.SelectionChange>();
        if (scenario == "totalCap")
        {
            // 999 files are already selected: adding Alpha's two would pass the 1,000-file total-selection cap.
            selected.UnionWith(Enumerable.Range(100_000, 999));
        }

        _api.OnGetSearchResultIds = scenario switch
        {
            "tooLarge" => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"type\":\"https://cachet.tracecounts.internal/errors/selection-too-large\",\"status\":400,\"limit\":1000}", System.Text.Encoding.UTF8, "application/json"),
            }),
            "serverError" => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            "unauthorized" => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            _ => BrowseFixtures.ServeResultIds,
        };
        var cut = RenderResults(selected: selected, changes: changes);

        // A file checkbox carries the searchResultId that browse returned for that row — nothing built by the Web.
        FolderRow(cut, "Alpha").Click();
        WaitForCurrent(cut, "Alpha");
        FileRow(cut, "a1.pdf").QuerySelector("input.fe-check").Should().NotBeNull();
        cut.Find("#fe-up").Click();
        WaitForCurrent(cut, "Archive");

        // A folder checkbox resolves its files through the Api only.
        FolderRow(cut, "Alpha").QuerySelector("input.fe-check")!.Change(true);

        ResultIdsCalls().Should().Equal("Alpha");
        switch (scenario)
        {
            case "ok":
                changes.Should().ContainSingle().Which.Should().BeEquivalentTo(new Explorer.SelectionChange(new[] { 9001, 9002 }, true));
                cut.FindAll(".fe-selection-message").Should().BeEmpty();
                FolderRow(cut, "Alpha").QuerySelector("input.fe-check")!.GetAttribute("data-state").Should().Be("checked");

                // Resolved ids are cached per folder: ticking again re-uses them (no second Api call), and
                // unticking removes exactly those ids.
                FolderRow(cut, "Alpha").QuerySelector("input.fe-check")!.Change(false);
                ResultIdsCalls().Should().Equal("Alpha");
                changes.Last().Should().BeEquivalentTo(new Explorer.SelectionChange(new[] { 9001, 9002 }, false));
                selected.Should().BeEmpty();
                break;
            case "tooLarge":
                changes.Should().BeEmpty("an over-cap selection selects nothing");
                cut.Find(".fe-selection-message").TextContent.Should().Be("Too many files selected (limit 1,000); narrow the selection.");
                FolderRow(cut, "Alpha").QuerySelector("input.fe-check")!.HasAttribute("checked").Should().BeFalse();
                break;
            case "serverError":
                changes.Should().BeEmpty("an Api error selects nothing");
                cut.Find(".fe-selection-message").TextContent.Should().Be("The selection could not be completed. Try again.");
                cut.Markup.Should().NotContain("500");
                break;
            case "unauthorized":
                changes.Should().BeEmpty();
                cut.Find(".fe-session-expired").TextContent.Should().Contain("Your session has expired");
                break;
            case "totalCap":
                changes.Should().BeEmpty("a selection that would pass the 1,000-file cap selects nothing");
                cut.Find(".fe-selection-message").TextContent.Should().Be("Too many files selected (limit 1,000); narrow the selection.");
                selected.Should().HaveCount(999);
                break;
        }
    }

    // Coverage-gap closure (not a spec-mapped ID): header checkbox, filtered header, folder keyboard and
    // selection while another selection is in flight.
    [Fact]
    public void Test_V9_FolderSelectionResolvedByApiOnly_HeaderCheckboxAndFilter()
    {
        var selected = new HashSet<int>();
        var changes = new List<Explorer.SelectionChange>();
        var cut = RenderResults(selected: selected, changes: changes);

        // Header = everything in the current folder (resolved through the Api for the folder), then cleared again.
        cut.Find("input.fe-check-all").Change(true);
        ResultIdsCalls().Should().Equal(string.Empty);
        selected.Should().BeEquivalentTo(new[] { 9001, 9002, 9003 });
        cut.Find("input.fe-check-all").GetAttribute("data-state").Should().Be("checked");
        cut.Find("input.fe-check-all").Change(false);
        selected.Should().BeEmpty();

        // With a filter active the header covers only the listed rows (each folder resolved through the Api).
        cut.Find("#fe-filter").Input("Alp");
        cut.Find("input.fe-check-all").Change(true);
        ResultIdsCalls().Should().Equal(string.Empty, "Alpha");
        selected.Should().BeEquivalentTo(new[] { 9001, 9002 });

        // Keyboard: Enter on a folder row opens it, other keys do not.
        cut.Find("#fe-filter").Input(string.Empty);
        FolderRow(cut, "Beta").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "a" });
        cut.Find(".fe-crumb-current").TextContent.Should().Be("Archive");
        FolderRow(cut, "Beta").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        WaitForCurrent(cut, "Beta");
        FileRow(cut, "b1.pdf").QuerySelector("input.fe-check")!.Change(true);
        selected.Should().Contain(9003);
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
