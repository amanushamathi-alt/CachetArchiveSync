using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Files;

/// <summary>
/// CAS-043 §13 shared HTTP harness: the real <c>Program.cs</c> pipeline (via
/// <see cref="FilesApiWebApplicationFactory"/>, same login idiom as
/// <c>FilesControllerIntegrationTests</c>) with only the two browse data sources substituted
/// (<see cref="IFileBrowseRepository"/> and <see cref="IB2PathPrefixProvider"/>). The real
/// <c>FileBrowseService</c>, <c>FilesController</c>, <c>FileContentService</c> and
/// <c>MirrorFileReader</c> run unmodified.
/// </summary>
internal sealed class BrowseHttpHarness : IAsyncDisposable
{
    public const string Prefix = "D/Shares/Operations";
    public const string B2Token = "4_secretB2token_neverLog";
    public const string B2ApiHost = "api-fake.backblazeb2.com";
    public const string B2KeyId = "plain-key-id";
    public const string B2AppKey = "plain-app-key";
    private const string Username = "jdoe";
    private const string Password = "correct horse battery staple";
    private const int UserId = 41;

    private static readonly PasswordHasher<object> Hasher = new();

    public FilesApiWebApplicationFactory Root { get; } = new();
    public IFileBrowseRepository Repository { get; } = Substitute.For<IFileBrowseRepository>();
    public IB2PathPrefixProvider PrefixProvider { get; } = Substitute.For<IB2PathPrefixProvider>();
    public WebApplicationFactory<Program> App { get; }
    public HttpClient Client { get; }

    public BrowseHttpHarness(string? bucketNameOverride = null)
    {
        PrefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Prefix);
        Repository.BrowseAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new FileBrowsePage { Folders = Array.Empty<FileBrowseFolderRow>(), Files = Array.Empty<FileBrowseFileRow>(), TotalFolders = 0, TotalFiles = 0 });

        App = Root.WithWebHostBuilder(builder =>
        {
            if (bucketNameOverride is not null)
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = bucketNameOverride }));
            }

            builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileBrowseRepository>();
            services.AddSingleton(Repository);
            services.RemoveAll<IB2PathPrefixProvider>();
            services.AddSingleton(PrefixProvider);
        });
        });
        Client = App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async Task<string> LoginAsync()
    {
        var version = Root.RegisterSessionTokenVersion(UserId);
        Root.UserRepository.GetByUsernameAsync(Username, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = UserId,
            Username = Username,
            PasswordHash = Hasher.HashPassword(new object(), Password),
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = version
        });
        Root.CredentialProtector.Unprotect("ct:keyid").Returns(B2KeyId);
        Root.CredentialProtector.Unprotect("ct:key").Returns(B2AppKey);
        Root.B2KeyAuthorizer
            .AuthorizeAsync(B2KeyId, B2AppKey, Arg.Any<CancellationToken>())
            .Returns(new B2AuthorizationInfo
            {
                AccountId = "acct-1",
                AuthorizationToken = B2Token,
                ApiUrl = "https://" + B2ApiHost,
                DownloadUrl = "https://download-fake.backblazeb2.com",
                Capabilities = new[] { "listFiles", "readFiles" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });

        var response = await Client.PostAsJsonAsync("/login", new LoginRequest { Username = Username, Password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var setCookie).Should().BeTrue();
        var cookie = setCookie!.First(v => v.StartsWith(FilesApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));

        // Only the endpoints under test count from here on.
        Root.AuditLogService.ClearReceivedCalls();
        return cookie.Split(';')[0];
    }

    public Task<HttpResponseMessage> GetAsync(
        string url, string? cookie,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return Client.SendAsync(request, completion, ct);
    }

    public List<AuditLogEntry> AuditEntries() =>
        Root.AuditLogService.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditLogService.WriteAsync))
            .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
            .ToList();

    /// <summary>Seeds a <c>dbo.file_index</c> record (substituted repository) and its mirror file.</summary>
    public void SeedFile(int fileIndexId, string extension)
    {
        var record = FileViewerHttpFixtures.Record(fileIndexId, extension, true);
        Root.FileIndexRepository.GetAsync(fileIndexId, Arg.Any<CancellationToken>()).Returns(record);
        var full = Path.Combine(Root.MirrorRoot, record.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, FileViewerHttpFixtures.ForExtension(extension));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        await Root.DisposeAsync();
    }
}
