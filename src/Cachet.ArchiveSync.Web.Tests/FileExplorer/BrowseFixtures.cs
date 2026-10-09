using System.Net;
using System.Net.Http.Json;
using System.Web;
using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Web.Tests.FileExplorer;

/// <summary>
/// CAS-044: canned <c>GET files/browse</c> (CAS-043 §6) bodies for the Web tests. The tree is
/// <c>Archive</c> (root) -> <c>Invoices</c> -> <c>Invoices/2025</c>, plus <c>Reports</c> -> <c>Reports/Q1</c>.
/// </summary>
internal static class BrowseFixtures
{
    public const string RootName = "Archive";

    public static readonly DateTimeOffset Modified = new(2025, 3, 14, 15, 30, 0, TimeSpan.Zero);

    public static BrowseFolderItem Folder(string path, bool hasSubfolders = true) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        Path = path,
        LatestModifiedAt = Modified,
        HasSubfolders = hasSubfolders,
    };

    public static BrowseFileItem File(int id, string name, string extension = "pdf", long size = 2048) => new()
    {
        FileIndexId = id,
        Name = name,
        Extension = extension,
        SizeBytes = size,
        ModifiedAt = Modified,
    };

    public static FolderBrowseResponse Listing(
        string folder,
        IReadOnlyList<BrowseFolderItem>? folders = null,
        IReadOnlyList<BrowseFileItem>? files = null)
    {
        var segments = folder.Length == 0 ? Array.Empty<string>() : folder.Split('/');
        var crumbs = new List<BreadcrumbItem>();
        for (var i = 0; i < segments.Length; i++)
        {
            crumbs.Add(new BreadcrumbItem { Name = segments[i], Path = string.Join('/', segments.Take(i + 1)) });
        }

        folders ??= Array.Empty<BrowseFolderItem>();
        files ??= Array.Empty<BrowseFileItem>();
        return new FolderBrowseResponse
        {
            RootName = RootName,
            Folder = folder,
            Parent = folder.Length == 0 ? null : (segments.Length == 1 ? string.Empty : string.Join('/', segments.Take(segments.Length - 1))),
            Breadcrumb = crumbs,
            Folders = folders,
            Files = files,
            TotalFolders = folders.Count,
            TotalFiles = files.Count,
        };
    }

    public static FolderBrowseResponse Root() => Listing(
        string.Empty,
        new[] { Folder("Invoices"), Folder("Reports") },
        new[] { File(101, "readme.txt", "txt", 300) });

    public static FolderBrowseResponse Invoices() => Listing(
        "Invoices",
        new[] { Folder("Invoices/2025") },
        new[] { File(201, "alpha-invoice.pdf"), File(202, "beta-ledger.xlsx", "xlsx", 5 * 1024 * 1024) });

    public static FolderBrowseResponse Invoices2025() => Listing(
        "Invoices/2025",
        null,
        new[] { File(301, "march.pdf") });

    public static FolderBrowseResponse Reports() => Listing(
        "Reports",
        new[] { Folder("Reports/Q1") },
        new[] { File(401, "summary.docx", "docx") });

    public static FolderBrowseResponse ReportsQ1() => Listing("Reports/Q1");

    /// <summary>Default fake Api: serves the fixture tree; an unknown folder is a 404.</summary>
    public static Task<HttpResponseMessage> Serve(HttpRequestMessage request)
    {
        var folder = Query(request).folder;
        FolderBrowseResponse? body = folder switch
        {
            "" => Root(),
            "Invoices" => Invoices(),
            "Invoices/2025" => Invoices2025(),
            "Reports" => Reports(),
            "Reports/Q1" => ReportsQ1(),
            _ => null,
        };

        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }

    public static (string folder, bool foldersOnly) Query(HttpRequestMessage request)
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        return (query["folder"] ?? string.Empty, string.Equals(query["foldersOnly"], "true", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------------------------
    // CAS-048: canned GET search/{taskId}/browse and .../browse/result-ids bodies. Results tree:
    //   (root)  -> Alpha, Beta
    //   Alpha   -> Alpha/Sub, a1.pdf (result 9001)
    //   Alpha/Sub -> a2.pdf (result 9002)
    //   Beta    -> b1.pdf (result 9003)
    // -------------------------------------------------------------------------------------------

    public static BrowseFileItem ResultFile(int fileIndexId, int searchResultId, string name) => new()
    {
        FileIndexId = fileIndexId,
        Name = name,
        Extension = "pdf",
        SizeBytes = 2048,
        ModifiedAt = Modified,
        SearchResultId = searchResultId,
    };

    public static FolderBrowseResponse ResultsRoot() => Listing(
        string.Empty,
        new[] { Folder("Alpha"), Folder("Beta", hasSubfolders: false) });

    public static FolderBrowseResponse ResultsAlpha() => Listing(
        "Alpha",
        new[] { Folder("Alpha/Sub", hasSubfolders: false) },
        new[] { ResultFile(201, 9001, "a1.pdf") });

    public static FolderBrowseResponse ResultsAlphaSub() => Listing(
        "Alpha/Sub",
        null,
        new[] { ResultFile(301, 9002, "a2.pdf") });

    public static FolderBrowseResponse ResultsBeta() => Listing(
        "Beta",
        null,
        new[] { ResultFile(401, 9003, "b1.pdf") });

    /// <summary>Default fake Api for <c>GET search/{taskId}/browse</c>: serves the results tree; unknown folder is 404.</summary>
    public static Task<HttpResponseMessage> ServeResults(HttpRequestMessage request, int taskId)
    {
        FolderBrowseResponse? body = Query(request).folder switch
        {
            "" => ResultsRoot(),
            "Alpha" => ResultsAlpha(),
            "Alpha/Sub" => ResultsAlphaSub(),
            "Beta" => ResultsBeta(),
            _ => null,
        };

        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }

    /// <summary>Default fake Api for <c>GET search/{taskId}/browse/result-ids</c> over the results tree.</summary>
    public static Task<HttpResponseMessage> ServeResultIds(HttpRequestMessage request, int taskId)
    {
        int[]? ids = Query(request).folder switch
        {
            "" => new[] { 9001, 9002, 9003 },
            "Alpha" => new[] { 9001, 9002 },
            "Alpha/Sub" => new[] { 9002 },
            "Beta" => new[] { 9003 },
            _ => null,
        };

        return Task.FromResult(ids is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SearchResultIdsResponse { SearchResultIds = ids, Count = ids.Length }),
            });
    }
}
