using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Http;
using Cachet.ArchiveSync.Web.Tests.KeywordSearch;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Explorer = Cachet.ArchiveSync.Web.Components.Shared.FileExplorer;

namespace Cachet.ArchiveSync.Web.Tests.FileExplorer;

/// <summary>
/// CAS-051 §13 — bUnit tests for the Files-mode quarantine checkboxes and folder select-all in
/// <c>FileExplorer.razor</c> (V1-V5, SC-1..SC-8) plus <c>QuarantineBrowseClient</c>. The "CachetApi"
/// client is redirected to <see cref="FakeKeywordSearchApiHandler"/>; a small in-memory server model
/// (<see cref="_quarantined"/>, <see cref="_folders"/>) answers the three CAS-050 endpoints. SC-9 and
/// SC-10 live in <c>KeywordSearchPageTests</c> (they need the host page).
/// </summary>
public sealed class FileExplorerQuarantineTests : TestContext
{
    private const string Bucket = "cachet-archive-prod";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly FakeKeywordSearchApiHandler _api = new();
    private readonly TestAuthorizationContext _auth;

    // In-memory server model for the CAS-050 endpoints.
    private readonly HashSet<int> _quarantined = new();
    private readonly Dictionary<string, (int Total, int Quarantined)> _folders = new(StringComparer.OrdinalIgnoreCase);
    private int _addedOverride = -1;
    private int _alreadyOverride = -1;
    private int _onQuarantineChanged;

    public FileExplorerQuarantineTests()
    {
        _api.OnGetFilesBrowse = BrowseFixtures.Serve;
        _api.OnGetQuarantineBrowseState = ServeState;
        _api.OnPostQuarantineBrowseAdd = ServeAdd;
        _api.OnPostQuarantineBrowseRemove = ServeRemove;

        var client = new HttpClient(_api) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(client));
        Services.AddTransient<FilesBrowseClient>();
        Services.AddTransient<SearchResultsBrowseClient>();
        Services.AddTransient<QuarantineBrowseClient>();
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = Bucket })
            .Build());
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(new TaskPollingOptions { TaskManagerPollIntervalSeconds = 1 }));
        _auth = this.AddTestAuthorization();
        _auth.SetAuthorized("full-access-user");
        _auth.SetClaims(new System.Security.Claims.Claim("b2_capability", "Full Access"));
    }

    // -----------------------------------------------------------------------------------------
    // Fake CAS-050 endpoints
    // -----------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(body) });

    private Task<HttpResponseMessage> ServeState(HttpRequestMessage request) =>
        Json(new QuarantineBrowseStateResponse
        {
            FileIndexIds = _quarantined.OrderBy(i => i).ToList(),
            Folders = _folders.Select(f => new QuarantineBrowseStateFolder
            {
                FolderName = f.Key,
                TotalFiles = f.Value.Total,
                QuarantinedFiles = f.Value.Quarantined,
            }).ToList(),
        });

    private async Task<HttpResponseMessage> ServeAdd(HttpRequestMessage request)
    {
        var body = (await request.Content!.ReadFromJsonAsync<QuarantineBrowseRequest>())!;
        if (body.Scope == "File")
        {
            var added = _quarantined.Add(body.FileIndexId!.Value) ? 1 : 0;
            return await Json(new QuarantineBrowseAddResponse { Added = added, AlreadyQuarantined = 1 - added });
        }

        var name = body.FolderPath!.Split('/')[^1];
        var (total, quarantined) = _folders[name];
        _folders[name] = (total, total);
        return await Json(new QuarantineBrowseAddResponse
        {
            Added = _addedOverride >= 0 ? _addedOverride : total - quarantined,
            AlreadyQuarantined = _alreadyOverride >= 0 ? _alreadyOverride : quarantined,
        });
    }

    private async Task<HttpResponseMessage> ServeRemove(HttpRequestMessage request)
    {
        var body = (await request.Content!.ReadFromJsonAsync<QuarantineBrowseRequest>())!;
        if (body.Scope == "File")
        {
            return await Json(new QuarantineBrowseRemoveResponse { Removed = _quarantined.Remove(body.FileIndexId!.Value) ? 1 : 0 });
        }

        var name = body.FolderPath!.Split('/')[^1];
        var (total, quarantined) = _folders[name];
        _folders[name] = (total, 0);
        return await Json(new QuarantineBrowseRemoveResponse { Removed = quarantined });
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private IRenderedComponent<Explorer> RenderExplorer(bool waitForQuarantineColumn = true)
    {
        var cut = RenderComponent<Explorer>(p => p.Add(
            x => x.OnQuarantineChanged,
            EventCallback.Factory.Create(this, () => { _onQuarantineChanged++; })));
        cut.WaitForState(() => cut.FindAll("tr.fe-row").Count > 0 || cut.FindAll(".fe-error, .fe-session-expired, .fe-empty").Count > 0, Wait);
        if (waitForQuarantineColumn)
        {
            cut.WaitForState(() => cut.FindAll("th.fe-cell-check").Count > 0, Wait);
        }

        return cut;
    }

    private void SeedRootFolders()
    {
        _folders["Invoices"] = (2, 0);
        _folders["Reports"] = (3, 3);
    }

    private static IElement FolderRow(IRenderedComponent<Explorer> cut, string name) =>
        cut.FindAll("tr.fe-row-folder").Single(r => r.QuerySelector(".fe-name")!.TextContent == name);

    private static IElement FileRow(IRenderedComponent<Explorer> cut, string name) =>
        cut.FindAll("tr.fe-row-file").Single(r => r.QuerySelector(".fe-name")!.TextContent == name);

    private static IElement FolderCheck(IRenderedComponent<Explorer> cut, string name) =>
        FolderRow(cut, name).QuerySelector("input.fe-check-quarantine")!;

    private static IElement FileCheck(IRenderedComponent<Explorer> cut, string name) =>
        FileRow(cut, name).QuerySelector("input.fe-check-quarantine")!;

    private static void WaitForCurrent(IRenderedComponent<Explorer> cut, string name) =>
        cut.WaitForAssertion(() => cut.Find(".fe-crumb-current").TextContent.Should().Be(name), Wait);

    private List<string> StateFolderPaths() =>
        _api.Requests.ToList()
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/quarantine/browse/state", StringComparison.Ordinal))
            .Select(r => System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["folderPath"] ?? string.Empty)
            .ToList();

    private List<HttpRequestMessage> PostsTo(string suffix) =>
        _api.Requests.ToList()
            .Where(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal))
            .ToList();

    private int WritePosts() => PostsTo("/quarantine/browse").Count + PostsTo("/quarantine/browse/remove").Count;

    private QuarantineBrowseRequest? LastBody(string suffix)
    {
        var index = _api.Requests.ToList().FindLastIndex(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal));
        return index < 0
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<QuarantineBrowseRequest>(_api.RequestBodies[index]!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    // -----------------------------------------------------------------------------------------
    // V1 / SC-5 - checkbox column only for Full Access in Files mode
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V1_ReadOnly_NoCheckboxes()
    {
        SeedRootFolders();
        _auth.SetClaims(new System.Security.Claims.Claim("b2_capability", "Read Only"));

        var cut = RenderExplorer(waitForQuarantineColumn: false);

        cut.FindAll("input.fe-check-quarantine").Should().BeEmpty();
        StateFolderPaths().Should().BeEmpty("a Read Only session never calls the Full Access state endpoint");
    }

    [Fact]
    public void Test_SC5_ReadOnly()
    {
        SeedRootFolders();
        _auth.SetClaims(new System.Security.Claims.Claim("b2_capability", "Read Only"));

        var cut = RenderExplorer(waitForQuarantineColumn: false);

        cut.FindAll("tr.fe-row").Count.Should().BeGreaterThan(0);
        cut.FindAll("input[type=checkbox]").Should().BeEmpty("no checkbox column in the DOM");
        cut.FindAll("th.fe-cell-check").Should().BeEmpty();
        cut.FindAll("col.fe-col-check").Should().BeEmpty();
        cut.Markup.Should().NotContainEquivalentOf("Quarantined");
    }

    [Fact]
    public void Test_V1_FullAccess_RendersCheckboxColumnInFilesMode()
    {
        SeedRootFolders();

        var cut = RenderExplorer();

        cut.FindAll("th.fe-cell-check").Count.Should().Be(1);
        FileCheck(cut, "readme.txt").GetAttribute("aria-label").Should().Be("Quarantine readme.txt");
        FolderCheck(cut, "Invoices").GetAttribute("aria-label").Should().Be("Select or deselect all files in Invoices");
        cut.FindAll("input.fe-check-all").Should().BeEmpty("no header select-all in Files mode (§2.6)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC1_CheckFile()
    {
        SeedRootFolders();
        var cut = RenderExplorer();
        FileCheck(cut, "readme.txt").HasAttribute("checked").Should().BeFalse();

        FileCheck(cut, "readme.txt").Change(true);

        cut.WaitForAssertion(() => FileCheck(cut, "readme.txt").HasAttribute("checked").Should().BeTrue("the row stays checked"), Wait);
        var body = LastBody("/quarantine/browse")!;
        body.Scope.Should().Be("File");
        body.FileIndexId.Should().Be(101);
        cut.Find(".fe-quarantine-message").TextContent.Should().Be("1 file quarantined, 0 already quarantined.");
        _onQuarantineChanged.Should().Be(1, "the host is notified so the panel/badge refresh");
        StateFolderPaths().Count.Should().Be(2, "the state is re-read after the action");

        // Unchecking calls remove for the same file.
        FileCheck(cut, "readme.txt").Change(false);
        cut.WaitForAssertion(() => FileCheck(cut, "readme.txt").HasAttribute("checked").Should().BeFalse(), Wait);
        LastBody("/quarantine/browse/remove")!.FileIndexId.Should().Be(101);
        cut.Find(".fe-quarantine-message").TextContent.Should().Be("1 file removed.");
    }

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC2_FolderSelectAll()
    {
        SeedRootFolders();
        var cut = RenderExplorer();
        FolderCheck(cut, "Invoices").GetAttribute("data-state").Should().Be("unchecked");

        FolderCheck(cut, "Invoices").Change(true);

        cut.Find("#fe-quarantine-dialog .fe-dialog-add").TextContent.Should().Be("Quarantine 2 files in Invoices and its sub-folders?");
        cut.FindAll(".fe-dialog-remove").Should().BeEmpty();
        cut.Find(".fe-dialog-note").TextContent.Should().Contain("Files added to this folder later are not included.");
        WritePosts().Should().Be(0, "nothing is sent before confirmation");

        cut.Find("#fe-dialog-select-all").Click();

        cut.WaitForAssertion(() => FolderCheck(cut, "Invoices").GetAttribute("data-state").Should().Be("checked"), Wait);
        var body = LastBody("/quarantine/browse")!;
        body.Scope.Should().Be("Folder");
        body.Source.Should().Be("B2");
        body.BucketName.Should().Be(Bucket);
        body.FolderPath.Should().Be("Invoices");
        cut.Find(".fe-quarantine-message").TextContent.Should().Be("2 files quarantined, 0 already quarantined.");
        cut.FindAll("#fe-quarantine-dialog").Should().BeEmpty();
        _onQuarantineChanged.Should().Be(1);
    }

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC3_FolderDeselectAll()
    {
        SeedRootFolders();
        var cut = RenderExplorer();
        FolderCheck(cut, "Reports").GetAttribute("data-state").Should().Be("checked");

        FolderCheck(cut, "Reports").Change(false);

        cut.Find("#fe-quarantine-dialog .fe-dialog-remove").TextContent.Should().Be(
            "Remove 3 files in Reports from Quarantine, including files quarantined from Keyword Search?");
        cut.FindAll(".fe-dialog-add").Should().BeEmpty();
        cut.FindAll("#fe-dialog-select-all").Should().BeEmpty();

        cut.Find("#fe-dialog-deselect-all").Click();

        cut.WaitForAssertion(() => FolderCheck(cut, "Reports").GetAttribute("data-state").Should().Be("unchecked"), Wait);
        var body = LastBody("/quarantine/browse/remove")!;
        body.Scope.Should().Be("Folder");
        body.FolderPath.Should().Be("Reports");
        body.BucketName.Should().Be(Bucket);
        cut.Find(".fe-quarantine-message").TextContent.Should().Be("3 files removed.");
        _onQuarantineChanged.Should().Be(1);
    }

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC4_PartialFolder_BothActions()
    {
        _folders["Invoices"] = (5, 2);
        _folders["Reports"] = (1, 0);
        var cut = RenderExplorer();

        var check = FolderCheck(cut, "Invoices");
        check.GetAttribute("data-state").Should().Be("mixed");
        check.GetAttribute("aria-checked").Should().Be("mixed");
        check.HasAttribute("checked").Should().BeFalse();

        check.Change(true);

        cut.Find(".fe-dialog-add").TextContent.Should().Contain("Quarantine 3 files in Invoices");
        cut.Find(".fe-dialog-remove").TextContent.Should().Contain("Remove 2 files in Invoices");
        cut.Find("#fe-dialog-select-all").TextContent.Should().Be("Select all");
        cut.Find("#fe-dialog-deselect-all").TextContent.Should().Be("Deselect all");

        // Singular wording for a single file.
        cut.Find("#fe-dialog-cancel").Click();
        FolderCheck(cut, "Reports").Change(true);
        cut.Find(".fe-dialog-add").TextContent.Should().Contain("Quarantine 1 file in Reports");
    }

    // -----------------------------------------------------------------------------------------
    // V2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V2_Toggle_DisablesWhileInFlight()
    {
        SeedRootFolders();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.OnPostQuarantineBrowseAdd = async request =>
        {
            await gate.Task;
            return await ServeAdd(request);
        };
        var cut = RenderExplorer();

        FileCheck(cut, "readme.txt").Change(true);

        cut.WaitForAssertion(() => FileCheck(cut, "readme.txt").HasAttribute("disabled").Should().BeTrue("disabled while the call is in flight"), Wait);
        cut.FindAll(".fe-quarantine-message").Should().BeEmpty("no success message before the 200");

        // A second toggle on the same row is ignored: still exactly one request.
        FileCheck(cut, "readme.txt").Change(false);
        PostsTo("/quarantine/browse").Count.Should().Be(1);
        PostsTo("/quarantine/browse/remove").Count.Should().Be(0);

        gate.SetResult(true);

        cut.WaitForAssertion(() => FileCheck(cut, "readme.txt").HasAttribute("disabled").Should().BeFalse(), Wait);
        FileCheck(cut, "readme.txt").HasAttribute("checked").Should().BeTrue();
        PostsTo("/quarantine/browse").Count.Should().Be(1);
    }

    // -----------------------------------------------------------------------------------------
    // V3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V3_FolderAction_RequiresConfirm()
    {
        SeedRootFolders();
        var cut = RenderExplorer();

        FolderCheck(cut, "Invoices").Change(true);
        cut.FindAll("#fe-quarantine-dialog").Count.Should().Be(1);
        WritePosts().Should().Be(0, "the folder action never calls the API without the dialog being confirmed");

        cut.Find("#fe-dialog-cancel").Click();

        cut.FindAll("#fe-quarantine-dialog").Should().BeEmpty();
        WritePosts().Should().Be(0, "cancel = no call");
        FolderCheck(cut, "Invoices").GetAttribute("data-state").Should().Be("unchecked");
        _onQuarantineChanged.Should().Be(0);
        cut.FindAll(".fe-quarantine-message").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // V4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public void Test_V4_ApiError_Reverts(HttpStatusCode status)
    {
        SeedRootFolders();
        _api.OnPostQuarantineBrowseAdd = _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"detail\":\"secret SQL path C:\\\\x\"}"),
        });
        var cut = RenderExplorer();
        var stateCallsBefore = StateFolderPaths().Count;

        FileCheck(cut, "readme.txt").Change(true);

        cut.WaitForAssertion(() => cut.Find(".fe-quarantine-message").TextContent.Should().Be("The quarantine action could not be completed. Try again."), Wait);
        cut.Find(".fe-quarantine-message").GetAttribute("role").Should().Be("alert");
        cut.Find(".fe-quarantine-message").TextContent.Should().NotContain("secret").And.NotContain("SQL");
        FileCheck(cut, "readme.txt").HasAttribute("checked").Should().BeFalse("the checkbox reverts");
        FileCheck(cut, "readme.txt").HasAttribute("disabled").Should().BeFalse();
        _onQuarantineChanged.Should().Be(0, "no state change is announced");
        StateFolderPaths().Count.Should().Be(stateCallsBefore, "no state change, so no re-read");
    }

    [Fact]
    public void Test_V4_FolderApiError_KeepsStateAndShowsGenericMessage()
    {
        SeedRootFolders();
        _api.OnPostQuarantineBrowseAdd = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cut = RenderExplorer();

        FolderCheck(cut, "Invoices").Change(true);
        cut.Find("#fe-dialog-select-all").Click();

        cut.WaitForAssertion(() => cut.Find(".fe-quarantine-message").TextContent.Should().Contain("could not be completed"), Wait);
        cut.FindAll("#fe-quarantine-dialog").Should().BeEmpty();
        FolderCheck(cut, "Invoices").GetAttribute("data-state").Should().Be("unchecked");
        _onQuarantineChanged.Should().Be(0);
    }

    // -----------------------------------------------------------------------------------------
    // V5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V5_FolderTriState()
    {
        _api.OnGetFilesBrowse = _ => Json(BrowseFixtures.Listing(string.Empty, new[]
        {
            BrowseFixtures.Folder("None"),
            BrowseFixtures.Folder("Some"),
            BrowseFixtures.Folder("Every"),
            BrowseFixtures.Folder("Empty"),
        }));
        _folders["None"] = (4, 0);
        _folders["Some"] = (4, 2);
        _folders["Every"] = (4, 4);
        _folders["Empty"] = (0, 0);

        var cut = RenderExplorer();

        FolderCheck(cut, "None").GetAttribute("data-state").Should().Be("unchecked");
        FolderCheck(cut, "None").HasAttribute("checked").Should().BeFalse();
        FolderCheck(cut, "Some").GetAttribute("data-state").Should().Be("mixed");
        FolderCheck(cut, "Some").GetAttribute("aria-checked").Should().Be("mixed");
        FolderCheck(cut, "Every").GetAttribute("data-state").Should().Be("checked");
        FolderCheck(cut, "Every").HasAttribute("checked").Should().BeTrue();
        FolderRow(cut, "Empty").QuerySelector("input").Should().BeNull("a folder with totalFiles = 0 shows no checkbox");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_StateReloaded()
    {
        SeedRootFolders();
        _folders["Q1"] = (1, 0);
        var cut = RenderExplorer();
        StateFolderPaths().Should().Equal(string.Empty);

        // List click -> Invoices (state is read for the new folder).
        FolderRow(cut, "Invoices").Click();
        WaitForCurrent(cut, "Invoices");
        cut.WaitForAssertion(() => StateFolderPaths().Should().Equal(string.Empty, "Invoices"), Wait);

        // "Up".
        cut.Find("#fe-up").Click();
        WaitForCurrent(cut, "Archive");
        cut.WaitForAssertion(() => StateFolderPaths().Should().Equal(string.Empty, "Invoices", string.Empty), Wait);

        // Tree click.
        cut.FindAll("button.fe-node-name").Single(b => b.TextContent == "Reports").Click();
        WaitForCurrent(cut, "Reports");
        cut.WaitForAssertion(() => StateFolderPaths().Should().Equal(string.Empty, "Invoices", string.Empty, "Reports"), Wait);

        // After a successful action the state is re-read once more.
        FileCheck(cut, "summary.docx").Change(true);
        cut.WaitForAssertion(() => StateFolderPaths().Should().Equal(string.Empty, "Invoices", string.Empty, "Reports", "Reports"), Wait);
        FileCheck(cut, "summary.docx").HasAttribute("checked").Should().BeTrue("state shown comes from the re-read, not an optimistic flip");
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_Filter_KeepsState()
    {
        _api.OnGetFilesBrowse = _ => Json(BrowseFixtures.Listing(
            string.Empty,
            new[] { BrowseFixtures.Folder("Alpha"), BrowseFixtures.Folder("Beta") },
            new[] { BrowseFixtures.File(1, "keep-1.txt", "txt"), BrowseFixtures.File(2, "drop-2.txt", "txt") }));
        _quarantined.Add(1);
        _folders["Alpha"] = (3, 0);
        _folders["Beta"] = (4, 1);
        var cut = RenderExplorer();

        cut.Find("#fe-filter").Input("keep");
        cut.WaitForAssertion(() => cut.FindAll("tr.fe-row").Count.Should().Be(1), Wait);
        FileCheck(cut, "keep-1.txt").HasAttribute("checked").Should().BeTrue("a filtered-in quarantined file stays checked");

        cut.Find("#fe-filter").Input("drop");
        cut.WaitForAssertion(() => cut.FindAll("tr.fe-row").Count.Should().Be(1), Wait);
        FileCheck(cut, "drop-2.txt").HasAttribute("checked").Should().BeFalse();

        cut.Find("#fe-filter").Input("bet");
        cut.WaitForAssertion(() => cut.FindAll("tr.fe-row").Count.Should().Be(1), Wait);
        FolderCheck(cut, "Beta").GetAttribute("data-state").Should().Be("mixed", "folder state is independent of the filter");

        // Acting on a filtered row targets that row's own file.
        cut.Find("#fe-filter").Input("drop");
        cut.WaitForAssertion(() => cut.FindAll("tr.fe-row").Count.Should().Be(1), Wait);
        FileCheck(cut, "drop-2.txt").Change(true);
        cut.WaitForAssertion(() => FileCheck(cut, "drop-2.txt").HasAttribute("checked").Should().BeTrue(), Wait);
        LastBody("/quarantine/browse")!.FileIndexId.Should().Be(2);
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_Virtualize_State()
    {
        const int total = 2500;
        var files = Enumerable.Range(1, total).Select(i => BrowseFixtures.File(i, $"file-{i:D4}.pdf")).ToList();
        _api.OnGetFilesBrowse = _ => Json(BrowseFixtures.Listing(string.Empty, null, files));
        foreach (var i in Enumerable.Range(1, total).Where(i => i % 2 == 0))
        {
            _quarantined.Add(i);
        }

        var cut = RenderExplorer();

        var rendered = cut.FindAll("tr.fe-row-file");
        rendered.Count.Should().BeInRange(1, total / 2, "Virtualize renders only the rows near the viewport");
        foreach (var row in rendered)
        {
            var id = int.Parse(row.GetAttribute("data-file-index-id")!, System.Globalization.CultureInfo.InvariantCulture);
            row.QuerySelector("input.fe-check-quarantine")!.HasAttribute("checked").Should().Be(id % 2 == 0, $"file {id} state comes from fileIndexIds");
        }

        StateFolderPaths().Count.Should().Be(1, "state is one call for the whole folder, independent of scrolling");
    }

    // -----------------------------------------------------------------------------------------
    // Coverage of failure / edge paths (no SC/V of their own)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_StateLoadFailure_HidesColumnWithGenericMessage()
    {
        _api.OnGetQuarantineBrowseState = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var cut = RenderExplorer(waitForQuarantineColumn: false);

        cut.WaitForAssertion(() => cut.Find(".fe-quarantine-message").TextContent.Should().Contain("could not be completed"), Wait);
        cut.FindAll("input.fe-check-quarantine").Should().BeEmpty("unknown state must not be rendered as unchecked");
    }

    [Fact]
    public void Test_StateLoad401_ShowsSessionExpired()
    {
        _api.OnGetQuarantineBrowseState = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var cut = RenderExplorer(waitForQuarantineColumn: false);

        cut.WaitForAssertion(() => cut.FindAll(".fe-session-expired").Count.Should().Be(1), Wait);
    }

    [Fact]
    public void Test_ActionApi401_ShowsSessionExpired()
    {
        SeedRootFolders();
        _api.OnPostQuarantineBrowseRemove = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var cut = RenderExplorer();

        FolderCheck(cut, "Reports").Change(false);
        cut.Find("#fe-dialog-deselect-all").Click();

        cut.WaitForAssertion(() => cut.FindAll(".fe-session-expired").Count.Should().Be(1), Wait);
    }

    [Fact]
    public void Test_FolderAddReportsAlreadyQuarantinedCount()
    {
        _folders["Invoices"] = (5, 2);
        _folders["Reports"] = (1, 0);
        _addedOverride = 3;
        _alreadyOverride = 2;
        var cut = RenderExplorer();

        FolderCheck(cut, "Invoices").Change(true);
        cut.Find("#fe-dialog-select-all").Click();

        cut.WaitForAssertion(() => cut.Find(".fe-quarantine-message").TextContent.Should().Be("3 files quarantined, 2 already quarantined."), Wait);
    }

    [Fact]
    public void Test_SecondFolderChangeWhileDialogOpen_IsIgnored()
    {
        SeedRootFolders();
        var cut = RenderExplorer();

        FolderCheck(cut, "Invoices").Change(true);
        FolderCheck(cut, "Reports").Change(false);

        cut.FindAll("#fe-quarantine-dialog").Count.Should().Be(1);
        cut.Find(".fe-dialog-add").TextContent.Should().Contain("Invoices", "the first dialog stays open");
    }

    // -----------------------------------------------------------------------------------------
    // QuarantineBrowseClient (typed client) - status mapping, no leakage
    // -----------------------------------------------------------------------------------------

    private static QuarantineBrowseClient ClientFor(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, out ILogger<QuarantineBrowseClient> logger)
    {
        var handler = new FakeKeywordSearchApiHandler
        {
            OnGetQuarantineBrowseState = respond,
            OnPostQuarantineBrowseAdd = respond,
            OnPostQuarantineBrowseRemove = respond,
        };
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/api/") });
        logger = Substitute.For<ILogger<QuarantineBrowseClient>>();
        return new QuarantineBrowseClient(factory, logger);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, QuarantineBrowseOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, QuarantineBrowseOutcome.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, QuarantineBrowseOutcome.Rejected)]
    [InlineData(HttpStatusCode.Conflict, QuarantineBrowseOutcome.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, QuarantineBrowseOutcome.Failed)]
    public async Task Test_Client_MapsStatusToOutcome(HttpStatusCode status, QuarantineBrowseOutcome expected)
    {
        var client = ClientFor(_ => Task.FromResult(new HttpResponseMessage(status)), out _);

        (await client.GetStateAsync("B2", Bucket, "x", CancellationToken.None)).Outcome.Should().Be(expected);
        (await client.AddAsync(new QuarantineBrowseRequest { Scope = "File", FileIndexId = 1 }, CancellationToken.None)).Outcome.Should().Be(expected);
        (await client.RemoveAsync(new QuarantineBrowseRequest { Scope = "File", FileIndexId = 1 }, CancellationToken.None)).Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task Test_Client_Success_ReturnsBodyAndEscapesQuery()
    {
        HttpRequestMessage? seen = null;
        var client = ClientFor(r =>
        {
            seen = r;
            return Json(new QuarantineBrowseStateResponse { FileIndexIds = new[] { 7 }, Folders = Array.Empty<QuarantineBrowseStateFolder>() });
        }, out _);

        var result = await client.GetStateAsync("B2", Bucket, "A B/C&d", CancellationToken.None);

        result.Outcome.Should().Be(QuarantineBrowseOutcome.Success);
        result.Body!.FileIndexIds.Should().Equal(7);
        seen!.RequestUri!.Query.Should().Contain("folderPath=A%20B%2FC%26d");
    }

    [Fact]
    public async Task Test_Client_NullBodyOrBadJson_IsFailedAndLogsNoPathOrBody()
    {
        var client = ClientFor(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") }), out var logger);
        (await client.GetStateAsync("B2", Bucket, "secret-folder", CancellationToken.None)).Outcome.Should().Be(QuarantineBrowseOutcome.Failed);

        var bad = ClientFor(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") }), out var badLogger);
        (await bad.GetStateAsync("B2", Bucket, "secret-folder", CancellationToken.None)).Outcome.Should().Be(QuarantineBrowseOutcome.Failed);

        foreach (var l in new[] { logger, badLogger })
        {
            l.ReceivedCalls().SelectMany(c => c.GetArguments()).Select(a => a?.ToString() ?? string.Empty)
                .Should().NotContain(a => a.Contains("secret-folder", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Test_Client_TransportFailure_IsFailed_CallerCancellationPropagates()
    {
        var client = ClientFor(_ => throw new HttpRequestException("boom http://x/secret-folder"), out _);
        (await client.AddAsync(new QuarantineBrowseRequest { Scope = "File", FileIndexId = 1 }, CancellationToken.None)).Outcome.Should().Be(QuarantineBrowseOutcome.Failed);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelling = ClientFor(_ => throw new TaskCanceledException(), out _);
        var act = () => cancelling.GetStateAsync("B2", Bucket, "x", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var timeout = ClientFor(_ => throw new TaskCanceledException(), out _);
        (await timeout.GetStateAsync("B2", Bucket, "x", CancellationToken.None)).Outcome.Should().Be(QuarantineBrowseOutcome.Failed);
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
