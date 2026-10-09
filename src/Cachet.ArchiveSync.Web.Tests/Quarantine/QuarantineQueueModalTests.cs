using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cachet.ArchiveSync.Web.Tests.Quarantine;

/// <summary>
/// CAS-024 §13 — proves every Success Criterion (SC-2..SC-8, SC-10) and Validation (V1..V4) for
/// <c>QuarantineQueueModal.razor</c> owned directly by this test class, via bUnit, with the
/// "CachetApi" named <see cref="HttpClient"/> redirected to <see cref="FakeQuarantineQueueApiHandler"/>
/// so no real Api process/DB is ever reached — matching <c>KeywordSearchPageTests</c>' (CAS-022) and
/// <c>ODriveSyncPageTests</c>' (CAS-023) own conventions for this test project. SC-1 (trigger button
/// on both host pages) and SC-9 (embedded panel refresh on KeywordSearch) are proven instead in
/// <c>KeywordSearchPageTests</c>/<c>ODriveSyncPageTests</c>, per this spec's own §13 test-class
/// mapping — this component has no host page of its own to trigger it from.
/// </summary>
public sealed class QuarantineQueueModalTests : TestContext
{
    private readonly FakeQuarantineQueueApiHandler _apiHandler = new();
    private readonly CapturingLoggerProvider _loggerProvider = new();
    private readonly TestAuthorizationContext _authContext;

    public QuarantineQueueModalTests()
    {
        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));

        _authContext = this.AddTestAuthorization();
        SetFullAccess();

        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));
    }

    private void SetFullAccess()
    {
        _authContext.SetAuthorized("full-access-user");
        _authContext.SetClaims(new Claim("b2_capability", "Full Access"));
    }

    private void SetReadOnly()
    {
        _authContext.SetAuthorized("read-only-user");
        _authContext.SetClaims(new Claim("b2_capability", "Read Only"));
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    private static string NormalizeWhitespace(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\s+", " ").Trim();

    private static QuarantineListItem SampleItem(int fileId, string? name = null, string status = "Flagged") => new()
    {
        SourceTab = "Keyword Search",
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = name ?? $"quarantined-file-{fileId}.pdf",
        Path = $"/o-drive/quarantined-file-{fileId}.pdf",
        QuarantinedOn = DateTimeOffset.UtcNow.AddDays(-1),
        Status = status,
    };

    private IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> RenderModal(EventCallback? onDeleteCompleted = null)
    {
        return onDeleteCompleted is null
            ? RenderComponent<Web.Components.Shared.QuarantineQueueModal>()
            : RenderComponent<Web.Components.Shared.QuarantineQueueModal>(
                parameters => parameters.Add(p => p.OnDeleteCompleted, onDeleteCompleted.Value));
    }

    private static async Task OpenAsync(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut) =>
        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

    private static IElement DeleteButton(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut) =>
        cut.FindAll(".cachet-qq-footer button").First(b => b.TextContent.Contains("Delete Permanently"));

    private static IElement[] RowCheckboxes(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut) =>
        cut.FindAll(".cachet-qq-table tbody tr input[type=checkbox]").ToArray();

    private static void CheckRowByMarker(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut, string nameMarker)
    {
        var row = cut.FindAll(".cachet-qq-table tbody tr").First(r => r.TextContent.Contains(nameMarker, StringComparison.OrdinalIgnoreCase));
        row.QuerySelector("input[type=checkbox]")!.Change(true);
    }

    private static IElement ConfirmDialogButton(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut, string labelContains) =>
        cut.FindAll(".cachet-qq-confirm-modal button").First(b => b.TextContent.Contains(labelContains, StringComparison.OrdinalIgnoreCase));

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC2_ModalOpenLoadsAndRendersQuarantineList()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(11, name: "invoice.pdf", status: "Flagged") },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.Markup.Should().Contain("Name / Path");
        cut.Markup.Should().Contain("Quarantined On");
        cut.Markup.Should().Contain("Status");
        cut.Markup.Should().Contain("invoice.pdf");
        cut.Markup.Should().Contain("/o-drive/quarantined-file-11.pdf");
        RowCheckboxes(cut).Should().ContainSingle("exactly the checkbox column represents selection — the checkbox itself is column 1");
        _apiHandler.GetQuarantineCallCount.Should().Be(1, "opening the modal must call GET /quarantine exactly once (§8.1/§8.2)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-3 / V-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_DeletePermanentlyReflectsSelectionCount()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(21), SampleItem(22) },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        var deleteButton = DeleteButton(cut);
        deleteButton.HasAttribute("disabled").Should().BeTrue("0 rows checked must leave the button disabled");
        deleteButton.TextContent.Should().Contain("Delete Permanently (0)");

        CheckRowByMarker(cut, "quarantined-file-21.pdf");

        deleteButton = DeleteButton(cut);
        deleteButton.HasAttribute("disabled").Should().BeFalse("≥1 row checked must enable the button");
        deleteButton.TextContent.Should().Contain("Delete Permanently (1)");

        CheckRowByMarker(cut, "quarantined-file-22.pdf");

        deleteButton = DeleteButton(cut);
        deleteButton.TextContent.Should().Contain("Delete Permanently (2)");
    }

    [Fact]
    public async Task Test_V1_DeletePermanentlyDisabledUntilRowChecked()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(31) },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        DeleteButton(cut).HasAttribute("disabled").Should().BeTrue();

        CheckRowByMarker(cut, "quarantined-file-31.pdf");

        DeleteButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // SC-4 / V-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC4_DeleteRequiresExplicitConfirmationBeforeApiCall()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(41) },
        }));
        var deleteCalled = false;
        _apiHandler.OnPostDelete = _ =>
        {
            deleteCalled = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse { Results = Array.Empty<QuarantineDeleteResultItem>() }));
        };

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "quarantined-file-41.pdf");

        DeleteButton(cut).Click();

        NormalizeWhitespace(cut.Markup).Should().Contain(
            "This will permanently delete 1 file(s) from both the local cache and Backblaze B2. This action cannot be undone.");
        deleteCalled.Should().BeFalse("Delete Permanently must never call POST /quarantine/delete before the dialog is confirmed");

        ConfirmDialogButton(cut, "Cancel").Click();

        deleteCalled.Should().BeFalse("canceling the dialog must send no request");
        cut.Markup.Should().NotContain("permanently delete", "the confirmation dialog must be dismissed on Cancel");
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (1)", "canceling must preserve the selection");
    }

    [Fact]
    public async Task Test_V2_DeleteNeverSubmittedWithoutExplicitConfirmation()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(42), SampleItem(43) },
        }));
        var deleteCalled = false;
        _apiHandler.OnPostDelete = _ =>
        {
            deleteCalled = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse { Results = Array.Empty<QuarantineDeleteResultItem>() }));
        };

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "quarantined-file-42.pdf");
        CheckRowByMarker(cut, "quarantined-file-43.pdf");

        DeleteButton(cut).Click();

        deleteCalled.Should().BeFalse("opening the confirmation dialog alone must never call POST /quarantine/delete, regardless of selection count");
        _apiHandler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/quarantine/delete", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_ConfirmedDeleteCallsApiWithConfirmTrueAndCorrectFileIds()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(51), SampleItem(52) },
        }));
        QuarantineDeleteRequest? captured = null;
        _apiHandler.OnPostDelete = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<QuarantineDeleteRequest>();
            return JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse
            {
                Results = new[] { new QuarantineDeleteResultItem { FileId = 51, Result = "Success" } },
            });
        };

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "quarantined-file-51.pdf");

        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        captured.Should().NotBeNull();
        captured!.Confirm.Should().BeTrue("§8.3 point 2 requires confirm: true on the submitted request");
        captured.FileIds.Should().BeEquivalentTo(new[] { 51 }, "exactly the selected fileIds, not every row in the list");
        cut.Markup.Should().NotContain("permanently delete", "the dialog must close once confirmed");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC6_PartialBatchIsolatedPerFileNoModalLevelError()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[]
            {
                SampleItem(61, name: "succeeds.pdf"),
                SampleItem(62, name: "fails.pdf"),
            },
        }));
        _apiHandler.OnPostDelete = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse
        {
            Results = new[]
            {
                new QuarantineDeleteResultItem { FileId = 61, Result = "Success" },
                new QuarantineDeleteResultItem { FileId = 62, Result = "Failed", Reason = "Delete failed — see Audit Log" },
            },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "succeeds.pdf");
        CheckRowByMarker(cut, "fails.pdf");

        // §8.2: after a delete attempt the modal always re-fetches — the succeeded row is dropped
        // by the server, the failed row remains (with its status snapshot unchanged).
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(62, name: "fails.pdf") },
        }));

        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        cut.Markup.Should().NotContain("succeeds.pdf", "a row whose delete succeeded must be removed from the list on refresh");
        cut.Markup.Should().Contain("fails.pdf", "a row whose delete failed must remain visible");
        cut.Markup.Should().Contain("Delete failed — see Audit Log", "the failed row must show its own inline error");
        cut.Markup.Should().NotContain(
            "Something went wrong. Please try again.",
            "a partial batch outcome must never surface a modal-level error banner (§8.4) — only the per-row inline error");
    }

    // -----------------------------------------------------------------------------------------
    // SC-7 / V-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC7_ReadOnlySessionCannotDeleteButCanView()
    {
        SetReadOnly();
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(71) },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.Markup.Should().Contain("quarantined-file-71.pdf", "viewing the list remains available without CachetFullAccess");

        var deleteButton = DeleteButton(cut);
        deleteButton.HasAttribute("disabled").Should().BeTrue("Delete Permanently must be disabled for a Read Only session");
        deleteButton.GetAttribute("title").Should().Be("Requires Full Access");

        // Defense in depth: even a checked row must never let a click open the confirmation dialog
        // for a Read Only session (OpenConfirm's own guard).
        var deleteCalled = false;
        _apiHandler.OnPostDelete = _ =>
        {
            deleteCalled = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse { Results = Array.Empty<QuarantineDeleteResultItem>() }));
        };
        CheckRowByMarker(cut, "quarantined-file-71.pdf");
        deleteButton.Click();

        cut.Markup.Should().NotContain("permanently delete");
        deleteCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Test_V3_DeletePermanentlyUnavailableWithoutFullAccess()
    {
        SetReadOnly();
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(72) },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        DeleteButton(cut).HasAttribute("disabled").Should().BeTrue();
        DeleteButton(cut).GetAttribute("title").Should().Be("Requires Full Access");
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC8_EmptyQuarantineShowsExplicitEmptyStateCopy()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        NormalizeWhitespace(cut.Markup).Should().Contain(
            "Quarantine is empty. Files sent to quarantine from Keyword Search or the Files browser appear here for final review before permanent deletion.");
        cut.Markup.Should().NotContain("<table", "a zero-result queue renders explanatory copy, not an empty table");
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs()
    {
        const string SensitiveB2Key = "raw-b2-appkey-marker-cas024-6a12";
        const string SensitiveSessionCookie = "session-cookie-marker-cas024-9d31";
        const string SensitiveFileContent = "raw-file-content-marker-cas024-5c02";

        // QuarantineQueueModal.razor never reads a key from configuration or handles raw file
        // content — prove the property holds structurally, same defensive pattern
        // KeywordSearchPageTests'/ODriveSyncPageTests' own SC-10/SC-13 tests already established.
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:ApplicationKey"] = SensitiveB2Key })
            .Build());

        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(81, name: "ordinary-file.pdf") },
        }));
        _apiHandler.OnPostDelete = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse
        {
            Results = new[] { new QuarantineDeleteResultItem { FileId = 81, Result = "Success" } },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "ordinary-file.pdf");
        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        cut.Markup.Should().NotContain(SensitiveB2Key);
        cut.Markup.Should().NotContain(SensitiveSessionCookie);
        cut.Markup.Should().NotContain(SensitiveFileContent);

        var logText = string.Join("\n", _loggerProvider.Messages);
        logText.Should().NotContain(SensitiveB2Key);
        logText.Should().NotContain(SensitiveSessionCookie);
        logText.Should().NotContain(SensitiveFileContent);
    }

    // -----------------------------------------------------------------------------------------
    // V-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(91) },
        }));
        _apiHandler.OnPostDelete = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "quarantined-file-91.pdf");

        var act = () => DeleteButton(cut).Click();
        act.Should().NotThrow();
        act = () => ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        act.Should().NotThrow("a 422 from POST /quarantine/delete must be surfaced as a modal-level error, never an unhandled exception");
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    [Fact]
    public async Task Test_V4_GetQuarantineServerErrorRendersInlineErrorNotCrash()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var cut = RenderModal();
        var act = async () => await OpenAsync(cut);

        await act.Should().NotThrowAsync();
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    // -----------------------------------------------------------------------------------------
    // Coverage-gap closures (not spec-mapped SC/V IDs) — same precedent as KeywordSearchPageTests'/
    // ODriveSyncPageTests' own follow-up closures, closing branches §10/§7 don't individually name
    // but §13's ≥85% line-coverage target requires.
    // -----------------------------------------------------------------------------------------

    /// <summary>Closes the coverage gap on <c>LoadAsync</c>'s catch(HttpRequestException/
    /// TaskCanceledException) branch.</summary>
    [Fact]
    public async Task Test_LoadApiUnreachableShowsGenericErrorNotCrash()
    {
        _apiHandler.OnGetQuarantine = _ => throw new HttpRequestException("simulated: Api unreachable");

        var cut = RenderModal();
        var act = async () => await OpenAsync(cut);

        await act.Should().NotThrowAsync();
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    /// <summary>Closes the coverage gap on <c>SubmitDeleteAsync</c>'s catch(HttpRequestException/
    /// TaskCanceledException) branch for the delete call itself.</summary>
    [Fact]
    public async Task Test_SubmitDeleteApiUnreachableShowsGenericErrorNotCrash()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(101) },
        }));
        _apiHandler.OnPostDelete = _ => throw new HttpRequestException("simulated: Api unreachable");

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "quarantined-file-101.pdf");

        DeleteButton(cut).Click();
        var act = () => ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        act.Should().NotThrow();
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    /// <summary>Closes the coverage gap on <c>ToggleSelection</c>'s uncheck branch and
    /// <c>OpenConfirm</c>'s empty-selection guard.</summary>
    [Fact]
    public async Task Test_UncheckingRowRemovesItFromSelectionAndDisablesDeleteAgain()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(111) },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, "quarantined-file-111.pdf");
        DeleteButton(cut).TextContent.Should().Contain("(1)");

        cut.Find(".cachet-qq-table tbody tr input[type=checkbox]").Change(false);

        DeleteButton(cut).TextContent.Should().Contain("(0)");
        DeleteButton(cut).HasAttribute("disabled").Should().BeTrue();

        var deleteCalled = false;
        _apiHandler.OnPostDelete = _ =>
        {
            deleteCalled = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse { Results = Array.Empty<QuarantineDeleteResultItem>() }));
        };
        DeleteButton(cut).Click();

        deleteCalled.Should().BeFalse("OpenConfirm must no-op when selectedIds is empty, even if clicked directly");
    }

    /// <summary>Closes the coverage gap on <c>OnDeleteCompleted</c> only being invoked when at least
    /// one row actually succeeded — a fully-failed batch must not invoke the parent's refresh
    /// callback.</summary>
    [Fact]
    public async Task Test_FullyFailedBatchDoesNotInvokeOnDeleteCompletedCallback()
    {
        var callbackInvoked = false;
        var callback = EventCallback.Factory.Create(this, () => callbackInvoked = true);

        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(121) },
        }));
        _apiHandler.OnPostDelete = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse
        {
            Results = new[] { new QuarantineDeleteResultItem { FileId = 121, Result = "Failed", Reason = "Delete failed — see Audit Log" } },
        }));

        var cut = RenderModal(callback);
        await OpenAsync(cut);
        CheckRowByMarker(cut, "quarantined-file-121.pdf");
        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        callbackInvoked.Should().BeFalse("OnDeleteCompleted must only fire when at least one row actually succeeded");
    }

    /// <summary>Closes the coverage gap on <c>Close</c> — dismisses the modal without side effects.</summary>
    [Fact]
    public async Task Test_CloseDismissesModalWithoutSideEffects()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleItem(131) },
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.FindAll(".cachet-qq-footer button").First(b => b.TextContent.Contains("Close")).Click();

        cut.Markup.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-052 — pagination, page-scoped selection, source tab (server-style paging fake)
    // -----------------------------------------------------------------------------------------

    private List<QuarantineListItem> ServeStore(int count, string sourceTab = "Keyword Search", List<(int Page, int PageSize)>? requests = null)
    {
        var store = QuarantinePagingFixtures.Store(count, sourceTab);
        _apiHandler.OnGetQuarantine = QuarantinePagingFixtures.Serve(store, requests);
        return store;
    }

    private static string Q(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut, string testId) =>
        cut.Find($"[data-testid=qq-{testId}]").TextContent.Trim();

    private static string[] RowNames(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut) =>
        cut.FindAll(".cachet-qq-table tbody tr button.cachet-file-link").Select(b => b.TextContent.Trim()).ToArray();

    private static string FileName(int id) => $"quarantined-file-{id}.pdf";

    private static void GoNext(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut) =>
        cut.Find("[data-testid=qq-next]").Click();

    private static void GoPrev(IRenderedComponent<Web.Components.Shared.QuarantineQueueModal> cut) =>
        cut.Find("[data-testid=qq-prev]").Click();

    private static string Normalized(string s) => NormalizeWhitespace(s);

    [Fact]
    public async Task Test_V1_PageBounds()
    {
        var requests = new List<(int Page, int PageSize)>();
        ServeStore(60, requests: requests);

        var cut = RenderModal();
        await OpenAsync(cut);

        // Page 1 of 3: Previous disabled; walk to the last page where Next is disabled.
        cut.Find("[data-testid=qq-prev]").HasAttribute("disabled").Should().BeTrue();
        GoNext(cut);
        GoNext(cut);
        Q(cut, "page-of").Should().Be("Page 3 of 3");
        cut.Find("[data-testid=qq-next]").HasAttribute("disabled").Should().BeTrue();

        // Only 25/50 are offered; an unsupported size does not reach the Api.
        cut.FindAll("[data-testid=qq-page-size] option").Select(o => o.GetAttribute("value")).Should().Equal("25", "50");
        var callsBefore = requests.Count;
        cut.Find("[data-testid=qq-page-size]").Change("10");
        requests.Count.Should().Be(callsBefore, "an unsupported page size must be ignored");

        // Every request stayed inside the validated bounds.
        requests.Should().OnlyContain(r => r.Page >= 1 && r.Page <= 3 && (r.PageSize == 25 || r.PageSize == 50));
    }

    [Fact]
    public async Task Test_V2_SelectAll_CurrentPageOnly()
    {
        ServeStore(60);

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.Find("[data-testid=qq-select-all-page]").Change(true);

        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (25)", "select-all must reach only the 25 visible rows, never all 60");
        RowCheckboxes(cut).Should().OnlyContain(c => c.HasAttribute("checked"));

        GoNext(cut);
        RowCheckboxes(cut).Should().OnlyContain(c => !c.HasAttribute("checked"), "page 2 rows were never selected");
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (25)");
    }

    [Fact]
    public async Task Test_V3_SourceTab_FromApi()
    {
        var store = new List<QuarantineListItem>
        {
            QuarantinePagingFixtures.Item(1, "Files", "Informational"),
            QuarantinePagingFixtures.Item(2, "Keyword Search"),
        };
        _apiHandler.OnGetQuarantine = QuarantinePagingFixtures.Serve(store);

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.FindAll("td.cachet-qq-source-tab").Select(td => td.TextContent.Trim()).Should().Equal("Files", "Keyword Search");
    }

    [Fact]
    public async Task Test_V4_Count_IsTotal()
    {
        ServeStore(137);

        var cut = RenderModal();
        await OpenAsync(cut);

        RowNames(cut).Should().HaveCount(25);
        Q(cut, "range").Should().Be("1–25 of 137", "the count is the API total, not the page length");
        Q(cut, "page-of").Should().Be("Page 1 of 6");
    }

    [Fact]
    public async Task Test_V5_SelectionLifecycle()
    {
        ServeStore(60);
        var cut = RenderModal();
        await OpenAsync(cut);

        // Survives a page change in both directions.
        CheckRowByMarker(cut, FileName(1));
        GoNext(cut);
        CheckRowByMarker(cut, FileName(26));
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (2)");
        GoPrev(cut);
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (2)");
        RowCheckboxes(cut).First().HasAttribute("checked").Should().BeTrue("the page-1 row stays ticked after returning to it");

        // Cleared on page-size change.
        cut.Find("[data-testid=qq-page-size]").Change("50");
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (0)");

        // Cleared (and defaults restored) on reopen.
        CheckRowByMarker(cut, FileName(3));
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (1)");
        cut.FindAll(".cachet-qq-footer button").First(b => b.TextContent.Contains("Close")).Click();
        await OpenAsync(cut);
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (0)");
        Q(cut, "range").Should().Be("1–25 of 60", "page and page size reset to defaults on open");
        cut.Find("[data-testid=qq-page-size]").GetAttribute("value").Should().Be("25");
    }

    [Fact]
    public async Task Test_V6_StaleSelection_Pruned()
    {
        // Rows deleted this session must not stay counted as selected after the reload.
        var store = ServeStore(3);
        var deletes = new List<QuarantineDeleteRequest>();
        _apiHandler.OnPostDelete = QuarantinePagingFixtures.ServeDelete(store, deletes);

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, FileName(1));
        CheckRowByMarker(cut, FileName(2));
        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        RowNames(cut).Should().Equal(FileName(3));
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (0)", "deleted ids must be gone from the selection before the count is shown");
        DeleteButton(cut).HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=qq-clear-selection]").Should().BeEmpty();

        // A follow-up delete carries only the freshly selected id, not the stale ones.
        CheckRowByMarker(cut, FileName(3));
        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();
        deletes.Should().HaveCount(2);
        deletes[1].FileIds.Should().BeEquivalentTo(new[] { 3 });
    }

    [Fact]
    public async Task Test_V6_EmptyQueue_PageOneOfOne()
    {
        ServeStore(0);

        var cut = RenderModal();
        await OpenAsync(cut);

        Q(cut, "page-of").Should().Be("Page 1 of 1");
        Q(cut, "range").Should().Be("0 of 0");
        Normalized(cut.Markup).Should().Contain("Quarantine is empty.");
        cut.Markup.Should().NotContain("Page 1 of 0");
    }

    [Fact]
    public async Task Test_SC1_Modal_FirstPage()
    {
        var requests = new List<(int Page, int PageSize)>();
        ServeStore(60, requests: requests);

        var cut = RenderModal();
        await OpenAsync(cut);

        RowNames(cut).Should().HaveCount(25);
        RowNames(cut).First().Should().Be(FileName(1));
        RowNames(cut).Last().Should().Be(FileName(25));
        Q(cut, "range").Should().Be("1–25 of 60");
        cut.Find("[data-testid=qq-next]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=qq-prev]").HasAttribute("disabled").Should().BeTrue();
        requests.Should().Equal((1, 25));
    }

    [Fact]
    public async Task Test_SC2_Modal_PagingAndSize()
    {
        ServeStore(60);
        var cut = RenderModal();
        await OpenAsync(cut);

        GoNext(cut);
        RowNames(cut).First().Should().Be(FileName(26));
        RowNames(cut).Last().Should().Be(FileName(50));
        RowNames(cut).Should().HaveCount(25);
        Q(cut, "range").Should().Be("26–50 of 60");

        cut.Find("[data-testid=qq-page-size]").Change("50");
        RowNames(cut).Should().HaveCount(50);
        RowNames(cut).First().Should().Be(FileName(1));
        RowNames(cut).Last().Should().Be(FileName(50));
        Q(cut, "range").Should().Be("1–50 of 60");
    }

    [Fact]
    public async Task Test_SC5_SourceTab_And_Selectable()
    {
        var store = new List<QuarantineListItem>
        {
            QuarantinePagingFixtures.Item(1, "Files", "Informational"),
            QuarantinePagingFixtures.Item(2, "Keyword Search"),
        };
        _apiHandler.OnGetQuarantine = QuarantinePagingFixtures.Serve(store);

        var cut = RenderModal();
        await OpenAsync(cut);

        var rows = cut.FindAll(".cachet-qq-table tbody tr");
        var filesRow = rows.First(r => r.TextContent.Contains(FileName(1)));
        var keywordRow = rows.First(r => r.TextContent.Contains(FileName(2)));

        filesRow.QuerySelector("td.cachet-qq-source-tab")!.TextContent.Trim().Should().Be("Files");
        filesRow.QuerySelector(".cachet-chip")!.TextContent.Trim().Should().Be("Informational");
        filesRow.QuerySelector(".cachet-chip")!.ClassList.Should().Contain("cachet-chip-informational");
        keywordRow.QuerySelector("td.cachet-qq-source-tab")!.TextContent.Trim().Should().Be("Keyword Search");

        // Working viewer link: the name click opens the viewer by FileIndexId.
        filesRow.QuerySelector("button.cachet-file-link")!.Click();
        cut.FindComponent<Web.Components.Shared.FileViewerModal>().Instance.FileIndexId.Should().Be(500_001);

        // Selectable delete checkbox.
        cut.FindAll(".cachet-qq-table tbody tr").First(r => r.TextContent.Contains(FileName(1)))
            .QuerySelector("input[type=checkbox]")!.Change(true);
        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (1)");
        DeleteButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Test_SC7_Delete_AcrossPages()
    {
        var store = ServeStore(26);
        var deletes = new List<QuarantineDeleteRequest>();
        _apiHandler.OnPostDelete = QuarantinePagingFixtures.ServeDelete(store, deletes);

        var cut = RenderModal();
        await OpenAsync(cut);
        CheckRowByMarker(cut, FileName(1));
        GoNext(cut);
        CheckRowByMarker(cut, FileName(26));

        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (2)");
        DeleteButton(cut).Click();
        Normalized(Q(cut, "confirm-selection")).Should().Be("2 selected (1 on other pages)");

        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        deletes.Should().ContainSingle().Which.FileIds.Should().BeEquivalentTo(new[] { 1, 26 });
        // Page 2 no longer exists (24 rows left) -> clamped to the new last page.
        Q(cut, "page-of").Should().Be("Page 1 of 1");
        Q(cut, "range").Should().Be("1–24 of 24");
        RowNames(cut).Should().HaveCount(24);
    }

    [Fact]
    public async Task Test_SC8_ReadOnly()
    {
        SetReadOnly();
        ServeStore(60);

        var cut = RenderModal();
        await OpenAsync(cut);

        DeleteButton(cut).HasAttribute("disabled").Should().BeTrue();
        DeleteButton(cut).GetAttribute("title").Should().Be("Requires Full Access");

        GoNext(cut);
        Q(cut, "range").Should().Be("26–50 of 60", "paging works for a Read Only session");
        RowNames(cut).First().Should().Be(FileName(26));
        DeleteButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Test_SC10_CrossPage_Confirm_And_Clear()
    {
        ServeStore(60);
        var cut = RenderModal();
        await OpenAsync(cut);

        cut.FindAll("[data-testid=qq-clear-selection]").Should().BeEmpty("Clear selection only shows when something is selected");

        CheckRowByMarker(cut, FileName(1));
        CheckRowByMarker(cut, FileName(2));
        GoNext(cut);
        CheckRowByMarker(cut, FileName(30));

        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (3)");
        DeleteButton(cut).Click();
        Normalized(Q(cut, "confirm-selection")).Should().Be("3 selected (2 on other pages)");
        ConfirmDialogButton(cut, "Cancel").Click();

        cut.Find("[data-testid=qq-clear-selection]").Click();

        DeleteButton(cut).TextContent.Should().Contain("Delete Permanently (0)");
        DeleteButton(cut).HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=qq-clear-selection]").Should().BeEmpty();
        GoPrev(cut);
        RowCheckboxes(cut).Should().OnlyContain(c => !c.HasAttribute("checked"));
    }

    [Fact]
    public async Task Test_SC11_Clamp_And_EmptyQueue()
    {
        // (a) Deleting every row -> page 1 of 1 with the empty-state copy.
        var all = ServeStore(3);
        _apiHandler.OnPostDelete = QuarantinePagingFixtures.ServeDelete(all);
        var cut = RenderModal();
        await OpenAsync(cut);
        cut.Find("[data-testid=qq-select-all-page]").Change(true);
        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        Q(cut, "page-of").Should().Be("Page 1 of 1");
        Q(cut, "range").Should().Be("0 of 0");
        Normalized(cut.Markup).Should().Contain("Quarantine is empty.");

        // (b) Deleting the last page's rows -> clamp to the new last page (not an empty page 3).
        var store = ServeStore(51);
        _apiHandler.OnPostDelete = QuarantinePagingFixtures.ServeDelete(store);
        await OpenAsync(cut);
        GoNext(cut);
        GoNext(cut);
        Q(cut, "page-of").Should().Be("Page 3 of 3");
        CheckRowByMarker(cut, FileName(51));
        DeleteButton(cut).Click();
        ConfirmDialogButton(cut, "Yes, delete permanently").Click();

        Q(cut, "page-of").Should().Be("Page 2 of 2");
        Q(cut, "range").Should().Be("26–50 of 50");
        RowNames(cut).Should().HaveCount(25);
    }

    [Fact]
    public async Task Test_SC13_Pager_Accessibility()
    {
        ServeStore(60);
        var cut = RenderModal();
        await OpenAsync(cut);

        cut.Find("[data-testid=qq-prev]").GetAttribute("aria-label").Should().Be("Previous page");
        cut.Find("[data-testid=qq-next]").GetAttribute("aria-label").Should().Be("Next page");
        cut.Find("[data-testid=qq-select-all-page]").GetAttribute("aria-label").Should().Be("Select all on this page");
    }

    /// <summary>CAS-052 §2.8: the modal raises <c>OnClosed</c> whenever it is closed.</summary>
    [Fact]
    public async Task Test_OnClosedCallbackInvokedOnClose()
    {
        ServeStore(1);
        var closed = 0;
        var cut = RenderComponent<Web.Components.Shared.QuarantineQueueModal>(
            p => p.Add(c => c.OnClosed, EventCallback.Factory.Create(this, () => closed++)));
        await OpenAsync(cut);

        cut.FindAll(".cachet-qq-footer button").First(b => b.TextContent.Contains("Close")).Click();

        closed.Should().Be(1);
    }

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — this component only ever
    /// requests the "CachetApi" named client, so no name-based routing is needed. Mirrors
    /// KeywordSearchPageTests'/ODriveSyncPageTests' own private helper for this test project.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
