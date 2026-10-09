using System.Net;
using System.Text.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Files;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-043 §13 — the <c>FileBrowser</c> context on the existing CAS-040 endpoints
/// (<c>/files/{id}</c>, <c>/view</c>, <c>/download</c>), through the REAL pipeline.
/// </summary>
public sealed class FilesControllerContextTests : IAsyncLifetime
{
    private BrowseHttpHarness _h = null!;

    public Task InitializeAsync()
    {
        _h = new BrowseHttpHarness();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    // V8
    [Theory]
    [InlineData("")]
    [InlineData("/view")]
    [InlineData("/download")]
    public async Task Test_V8_FileBrowserContextAcceptedAndUnknownStillRejected(string suffix)
    {
        var cookie = await _h.LoginAsync();
        _h.SeedFile(2, "pdf");

        using var accepted = await _h.GetAsync($"/files/2{suffix}?context=FileBrowser", cookie);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var bad in new string?[] { null, "", "filebrowser", "FILEBROWSER", "Browser", "Reports" })
        {
            var url = $"/files/2{suffix}" + (bad is null ? string.Empty : $"?context={Uri.EscapeDataString(bad)}");
            using var rejected = await _h.GetAsync(url, cookie);

            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"context '{bad}' is not in the allowlist");
            using var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("type").GetString().Should().EndWith("/validation-failed");
            json.RootElement.GetProperty("title").GetString().Should().Contain("FileBrowser");
        }
    }

    // SC-11
    [Theory]
    [InlineData("view", AuditAction.FileViewed)]
    [InlineData("download", AuditAction.FileDownloaded)]
    public async Task Test_SC11_FileBrowserContextIsAuditedBeforeContent(string operation, AuditAction expectedAction)
    {
        var cookie = await _h.LoginAsync();
        _h.SeedFile(11, "pdf");

        // Hold the audit write open: no response header or content byte may be produced while the
        // audit row is not yet written (FR-23).
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auditStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Root.AuditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                auditStarted.TrySetResult();
                return gate.Task;
            });

        var pending = _h.GetAsync($"/files/11/{operation}?context=FileBrowser", cookie, HttpCompletionOption.ResponseHeadersRead);

        await auditStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        pending.IsCompleted.Should().BeFalse("no header or content byte may precede the audit write");

        gate.SetResult(1);
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();

        var entry = _h.AuditEntries().Should().ContainSingle().Subject;
        entry.Action.Should().Be(expectedAction);
        entry.Result.Should().Be(AuditResult.Success);
        entry.TabModule.Should().Be("Keyword Search");
        entry.ResultDetail.Should().Contain("context=FileBrowser");
    }

    // SC-11 (second sentence): the other three contexts produce the same result_detail as before.
    [Theory]
    [InlineData("KeywordSearch", "Keyword Search")]
    [InlineData("ODriveSync", "O-Drive Sync")]
    [InlineData("Quarantine", "Quarantine")]
    public async Task Test_SC11_FileBrowserContextIsAuditedBeforeContent_OtherContextsUnchanged(string context, string tabModule)
    {
        var cookie = await _h.LoginAsync();
        _h.SeedFile(12, "pdf");

        using var response = await _h.GetAsync($"/files/12/view?context={context}", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = _h.AuditEntries().Should().ContainSingle().Subject;
        entry.TabModule.Should().Be(tabModule);
        entry.ResultDetail.Should().NotContain("context=");
    }
}
