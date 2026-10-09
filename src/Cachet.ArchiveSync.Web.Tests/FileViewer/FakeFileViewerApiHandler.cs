using System.Net;
using System.Text.RegularExpressions;

namespace Cachet.ArchiveSync.Web.Tests.FileViewer;

/// <summary>
/// CAS-040: stands in for Api behind the "CachetApi" named <see cref="HttpClient"/> for the bUnit
/// File Viewer tests — <c>GET files/{id}?context=</c> (FileViewerModal's metadata call) plus the
/// read-only routes the three host screens call on load (<c>GET /quarantine</c>,
/// <c>GET /tasks[/{id}]</c>, <c>GET /search/{id}/results</c>, <c>GET /sync/{id}/results</c>), so the
/// seven §8.2 panels can be populated. Any route with no scenario configured fails loudly (502).
/// </summary>
public sealed class FakeFileViewerApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetFileMetadata { get; set; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetQuarantine { get; set; }
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetTask { get; set; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetSearchResults { get; set; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetSyncResults { get; set; }

    // CAS-048: GET search/{id}/browse (the results explorer on the Keyword Search screen).
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetSearchBrowse { get; set; }

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (request.Method == HttpMethod.Get)
        {
            var file = Regex.Match(path, @"/files/(?<id>\d+)$");
            if (file.Success && OnGetFileMetadata is not null)
            {
                return await OnGetFileMetadata(request, int.Parse(file.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            if (path.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase) && OnGetQuarantine is not null)
            {
                return await OnGetQuarantine(request);
            }

            var searchBrowse = Regex.Match(path, @"/search/(?<id>\d+)/browse$");
            if (searchBrowse.Success && OnGetSearchBrowse is not null)
            {
                return await OnGetSearchBrowse(request, int.Parse(searchBrowse.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            var task = Regex.Match(path, @"/tasks/(?<id>\d+)$");
            if (task.Success && OnGetTask is not null)
            {
                return await OnGetTask(request, int.Parse(task.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            if (Regex.IsMatch(path, @"/search/\d+/results$") && OnGetSearchResults is not null)
            {
                return await OnGetSearchResults(request);
            }

            if (Regex.IsMatch(path, @"/sync/\d+/results$") && OnGetSyncResults is not null)
            {
                return await OnGetSyncResults(request);
            }
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/acknowledge", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = $"FakeFileViewerApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }
}
