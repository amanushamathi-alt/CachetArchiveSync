using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Web.Tests.Quarantine;

/// <summary>
/// CAS-052: server-style paging stand-in for <c>GET /quarantine?page=&amp;pageSize=</c> (CAS-050 §8). The
/// store is ordered as given (the real Api orders by created_at, entry id); an out-of-range page returns
/// no rows with the true <c>TotalCount</c>, which is what drives the Web's clamp-to-last-page behaviour.
/// </summary>
internal static class QuarantinePagingFixtures
{
    public static QuarantineListItem Item(int fileId, string sourceTab = "Keyword Search", string status = "Flagged") => new()
    {
        SourceTab = sourceTab,
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = $"quarantined-file-{fileId}.pdf",
        Path = $"/o-drive/quarantined-file-{fileId}.pdf",
        QuarantinedOn = DateTimeOffset.UtcNow.AddDays(-1),
        Status = status,
    };

    public static List<QuarantineListItem> Store(int count, string sourceTab = "Keyword Search") =>
        Enumerable.Range(1, count).Select(id => Item(id, sourceTab)).ToList();

    /// <summary>Handler for <c>OnGetQuarantine</c>. Records each (page, pageSize) requested in
    /// <paramref name="requests"/> when supplied.</summary>
    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> Serve(
        List<QuarantineListItem> store, List<(int Page, int PageSize)>? requests = null) =>
        request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.TryParse(query["page"], out var p) ? p : 1;
            var pageSize = int.TryParse(query["pageSize"], out var s) ? s : 25;
            requests?.Add((page, pageSize));

            var rows = store.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new QuarantineListResponse
                {
                    TotalCount = store.Count,
                    Page = page,
                    PageSize = pageSize,
                    Items = rows,
                }),
            });
        };

    /// <summary>Handler for <c>POST /quarantine/delete</c> that removes the requested ids from the store
    /// and reports each as Success (the server-confirmed state the Web re-fetches).</summary>
    public static Func<HttpRequestMessage, Task<HttpResponseMessage>> ServeDelete(
        List<QuarantineListItem> store, List<QuarantineDeleteRequest>? captured = null) =>
        async request =>
        {
            var body = (await request.Content!.ReadFromJsonAsync<QuarantineDeleteRequest>())!;
            captured?.Add(body);
            store.RemoveAll(i => body.FileIds.Contains(i.FileId));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new QuarantineDeleteResponse
                {
                    Results = body.FileIds.Select(id => new QuarantineDeleteResultItem { FileId = id, Result = "Success" }).ToArray(),
                }),
            };
        };
}
