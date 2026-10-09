using System.Net;
using Bunit;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-060 / CAS-061 / CAS-063 §10 — after a completed "Copy to B2", "Update in B2" or "Purge from
/// B2", <c>ODriveSyncContentScan.razor</c> calls <c>POST /sync/mirror-sync</c> once (retrying on 409);
/// never for a failed action.
/// Same harness as <see cref="ODriveSyncActionProgressTests"/> (1 s poll interval).
/// </summary>
public sealed class ODriveSyncCopyMirrorSyncTests : ODriveSyncPagedTestBase
{
    private const int CopyTaskId = 7200;
    private const int MirrorTaskId = 7300;

    private static void Act(IRenderedFragment cut, Action action) =>
        cut.InvokeAsync(action).GetAwaiter().GetResult();

    private static string ToastText(IRenderedFragment cut) =>
        cut.FindAll(".cachet-sync-toast").Select(e => e.TextContent.Trim()).FirstOrDefault() ?? string.Empty;

    private void UseMaxAttempts(int attempts) =>
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(new TaskPollingOptions
        {
            TaskManagerPollIntervalSeconds = 1,
            MirrorSyncMaxAttempts = attempts,
        }));

    /// <summary>The given action task reports <paramref name="finalStatus"/> on its first poll; every
    /// other task id falls through to the scan's own handler.</summary>
    private void ActionTaskEndsWith(int taskId, string type, string finalStatus)
    {
        var baseOnGetTask = ApiHandler.OnGetTask!;
        ApiHandler.OnGetTask = (request, id) => id == taskId
            ? Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(taskId, type, finalStatus)))
            : baseOnGetTask(request, id);
    }

    private void CopyFirstNewRow(IRenderedFragment cut)
    {
        Act(cut, () => RowCheckboxes(cut, NewHeading)[0].Change(true));
        Act(cut, () => ActionButton(cut, NewHeading, "Copy to B2").Click());
    }

    [Fact]
    public void Test_CAS060_CopyCompleted_TriggersMirrorSync()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Completed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        CopyFirstNewRow(cut);

        cut.WaitForState(() => ToastText(cut).Contains($"Task #{MirrorTaskId}", StringComparison.Ordinal), PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
        ToastText(cut).Should().Be($"B2 mirror refresh started (Task #{MirrorTaskId}).");
    }

    [Fact]
    public void Test_CAS060_CopyFailed_DoesNotTriggerMirrorSync()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Failed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        CopyFirstNewRow(cut);

        // The poll ends when the Copy button is usable again (in-flight cleared).
        Act(cut, () => RowCheckboxes(cut, NewHeading)[1].Change(true));
        cut.WaitForState(() => !ActionButton(cut, NewHeading, "Copy to B2").HasAttribute("disabled"), PollWaitTimeout);
        Thread.Sleep(1500); // one more poll interval: no late mirror-sync call
        ApiHandler.MirrorSyncCallCount.Should().Be(0);
    }

    // ---- CAS-063: a completed Update also calls POST /sync/mirror-sync ------------------------------
    // (replaces CAS-060/CAS-061's Test_CAS060_Update_DoesNotTriggerMirrorSync)

    private const int UpdateTaskId = 7401;

    private void UpdateFirstModifiedRow(IRenderedFragment cut)
    {
        Act(cut, () => RowCheckboxes(cut, ModifiedHeading)[0].Change(true));
        Act(cut, () => ActionButton(cut, ModifiedHeading, "Update in B2").Click());
    }

    [Fact]
    public void Test_CAS063_UpdateCompleted_TriggersMirrorSync()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 0, modifiedTotal: 10);
        var baseOnGetTask = ApiHandler.OnGetTask!;
        ApiHandler.OnGetTask = (request, id) =>
        {
            if (id != UpdateTaskId)
            {
                return baseOnGetTask(request, id);
            }

            ApiHandler.ModifiedTotal = 9; // the updated row is Synced now
            return Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(UpdateTaskId, "SyncUpdate", "Completed")));
        };
        ApiHandler.OnPostUpdate = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncUpdate", UpdateTaskId)));
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        UpdateFirstModifiedRow(cut);

        // Existing Update behaviour unchanged (Modified panel reloads), then exactly one mirror-sync call.
        cut.WaitForState(() => ToastText(cut).Contains($"Task #{MirrorTaskId}", StringComparison.Ordinal), PollWaitTimeout);
        PanelHeading.Count(cut, ModifiedHeading).Should().Be("9");
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
    }

    [Fact]
    public void Test_CAS063_UpdateFailed_DoesNotTriggerMirrorSync()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 0, modifiedTotal: 10);
        ApiHandler.OnPostUpdate = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncUpdate", UpdateTaskId)));
        ActionTaskEndsWith(UpdateTaskId, "SyncUpdate", "Failed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        UpdateFirstModifiedRow(cut);

        cut.WaitForState(() => ApiHandler.Requests.ToList().Any(r => r.Method == HttpMethod.Get
            && r.RequestUri!.AbsolutePath.EndsWith($"/tasks/{UpdateTaskId}", StringComparison.Ordinal)), PollWaitTimeout);
        Thread.Sleep(1500);
        ApiHandler.MirrorSyncCallCount.Should().Be(0);
    }

    [Fact]
    public void Test_CAS063_UpdateMirrorSyncError_MessageSaysUpdated()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 0, modifiedTotal: 10);
        ApiHandler.OnPostUpdate = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncUpdate", UpdateTaskId)));
        ActionTaskEndsWith(UpdateTaskId, "SyncUpdate", "Completed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        UpdateFirstModifiedRow(cut);

        cut.WaitForState(() => ToastText(cut) == "Files updated. The B2 mirror could not be refreshed automatically.", PollWaitTimeout);
        cut.FindAll(".cachet-banner-error").Should().BeEmpty();
    }

    [Fact]
    public void Test_CAS063_ResumedUpdate_TriggersMirrorSync()
    {
        // The page opens while the user's Update task is still Running (ResumePendingActionAsync).
        ApiHandler.UsePagedResults(removedTotal: 0, newTotal: 0, modifiedTotal: 10);
        var scan = TaskDetail(ScanTaskId, "ContentScan", "Completed");
        ApiHandler.OnGetTasks = request =>
        {
            var query = request.RequestUri!.Query;
            var isUpdateQuery = query.Contains("type=SyncUpdate", StringComparison.OrdinalIgnoreCase);
            var isOtherAction = query.Contains("type=Sync", StringComparison.OrdinalIgnoreCase) && !isUpdateQuery;
            var item = isUpdateQuery
                ? new TaskListItem { TaskId = UpdateTaskId, Type = "SyncUpdate", SubmittedBy = scan.SubmittedBy, SubmittedAt = scan.SubmittedAt, Status = "Running", IsAcknowledged = true }
                : new TaskListItem { TaskId = scan.TaskId, Type = scan.Type, SubmittedBy = scan.SubmittedBy, SubmittedAt = scan.SubmittedAt, StartTime = scan.StartTime, CompletedAt = scan.CompletedAt, Status = scan.Status, IsAcknowledged = true };
            return Task.FromResult(Json(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = isOtherAction ? Array.Empty<TaskListItem>() : new[] { item },
                Page = 1,
                PageSize = 1,
                TotalCount = isOtherAction ? 0 : 1,
            }));
        };
        ApiHandler.OnGetTask = (_, id) => Task.FromResult(id switch
        {
            ScanTaskId => Json(HttpStatusCode.OK, scan),
            UpdateTaskId => Json(HttpStatusCode.OK, TaskDetail(UpdateTaskId, "SyncUpdate", "Completed")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        var cut = RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();

        cut.WaitForState(() => ToastText(cut).Contains($"Task #{MirrorTaskId}", StringComparison.Ordinal), PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
    }

    // ---- CAS-061: a completed Purge also calls POST /sync/mirror-sync ---------------------------------

    private const int PurgeTaskId = 7500;

    private void PurgeFirstRemovedRow(IRenderedFragment cut)
    {
        Act(cut, () => RowCheckboxes(cut, RemovedHeading)[0].Change(true));
        Act(cut, () => ActionButton(cut, RemovedHeading, "Purge from B2").Click());
        Act(cut, () => cut.FindAll(".cachet-confirm button").First(b => b.TextContent.Trim() == "Confirm").Click());
    }

    [Fact]
    public void Test_CAS061_PurgeCompleted_TriggersMirrorSync()
    {
        var cut = RenderCompletedScan(removedTotal: 10, newTotal: 0, modifiedTotal: 0);
        var baseOnGetTask = ApiHandler.OnGetTask!;
        ApiHandler.OnGetTask = (request, id) =>
        {
            if (id != PurgeTaskId)
            {
                return baseOnGetTask(request, id);
            }

            ApiHandler.RemovedTotal = 9; // the purged row is Synced now
            return Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(PurgeTaskId, "SyncPurge", "Completed")));
        };
        ApiHandler.OnPostPurge = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncPurge", PurgeTaskId)));
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        PurgeFirstRemovedRow(cut);

        // The existing Purge behaviour is unchanged (panel reloads to the remaining rows), then exactly
        // one mirror-sync call is made.
        cut.WaitForState(() => ToastText(cut).Contains($"Task #{MirrorTaskId}", StringComparison.Ordinal), PollWaitTimeout);
        PanelHeading.Count(cut, RemovedHeading).Should().Be("9");
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
    }

    [Fact]
    public void Test_CAS061_PurgeFailed_DoesNotTriggerMirrorSync()
    {
        var cut = RenderCompletedScan(removedTotal: 10, newTotal: 0, modifiedTotal: 0);
        ApiHandler.OnPostPurge = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncPurge", PurgeTaskId)));
        ActionTaskEndsWith(PurgeTaskId, "SyncPurge", "Failed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        PurgeFirstRemovedRow(cut);

        cut.WaitForState(() => ApiHandler.Requests.ToList().Any(r => r.Method == HttpMethod.Get
            && r.RequestUri!.AbsolutePath.EndsWith($"/tasks/{PurgeTaskId}", StringComparison.Ordinal)), PollWaitTimeout);
        Thread.Sleep(1500);
        ApiHandler.MirrorSyncCallCount.Should().Be(0);
    }

    [Fact]
    public void Test_CAS061_PurgeMirrorSyncError_MessageSaysPurged()
    {
        var cut = RenderCompletedScan(removedTotal: 10, newTotal: 0, modifiedTotal: 0);
        ApiHandler.OnPostPurge = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncPurge", PurgeTaskId)));
        ActionTaskEndsWith(PurgeTaskId, "SyncPurge", "Completed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        PurgeFirstRemovedRow(cut);

        cut.WaitForState(() => ToastText(cut) == "Files purged. The B2 mirror could not be refreshed automatically.", PollWaitTimeout);
        cut.FindAll(".cachet-banner-error").Should().BeEmpty();
    }

    [Fact]
    public void Test_CAS061_ResumedPurge_TriggersMirrorSync()
    {
        // The page opens while the user's Purge task is still Running (ResumePendingActionAsync).
        ApiHandler.UsePagedResults(removedTotal: 10, newTotal: 0, modifiedTotal: 0);
        var scan = TaskDetail(ScanTaskId, "ContentScan", "Completed");
        ApiHandler.OnGetTasks = request =>
        {
            var query = request.RequestUri!.Query;
            var isPurgeQuery = query.Contains("type=SyncPurge", StringComparison.OrdinalIgnoreCase);
            var isOtherAction = query.Contains("type=Sync", StringComparison.OrdinalIgnoreCase) && !isPurgeQuery;
            var item = isPurgeQuery
                ? new TaskListItem { TaskId = PurgeTaskId, Type = "SyncPurge", SubmittedBy = scan.SubmittedBy, SubmittedAt = scan.SubmittedAt, Status = "Running", IsAcknowledged = true }
                : new TaskListItem { TaskId = scan.TaskId, Type = scan.Type, SubmittedBy = scan.SubmittedBy, SubmittedAt = scan.SubmittedAt, StartTime = scan.StartTime, CompletedAt = scan.CompletedAt, Status = scan.Status, IsAcknowledged = true };
            return Task.FromResult(Json(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = isOtherAction ? Array.Empty<TaskListItem>() : new[] { item },
                Page = 1,
                PageSize = 1,
                TotalCount = isOtherAction ? 0 : 1,
            }));
        };
        ApiHandler.OnGetTask = (_, id) => Task.FromResult(id switch
        {
            ScanTaskId => Json(HttpStatusCode.OK, scan),
            PurgeTaskId => Json(HttpStatusCode.OK, TaskDetail(PurgeTaskId, "SyncPurge", "Completed")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        var cut = RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();

        cut.WaitForState(() => ToastText(cut).Contains($"Task #{MirrorTaskId}", StringComparison.Ordinal), PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
    }

    [Fact]
    public void Test_CAS060_MirrorSyncConflict_RetriedUntilAccepted()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Completed");
        var attempts = 0;
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(++attempts < 3
            ? new HttpResponseMessage(HttpStatusCode.Conflict)
            : Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        CopyFirstNewRow(cut);

        cut.WaitForState(() => ToastText(cut).Contains($"Task #{MirrorTaskId}", StringComparison.Ordinal), PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(3);
    }

    [Fact]
    public void Test_CAS060_MirrorSyncConflict_GivesUpWithMessage()
    {
        UseMaxAttempts(2);
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Completed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        CopyFirstNewRow(cut);

        cut.WaitForState(() => ToastText(cut).Contains("next B2 Mirror Sync", StringComparison.Ordinal), PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(2);
        cut.FindAll(".cachet-banner-error").Should().BeEmpty("a busy mirror sync is informational, not an error");
    }

    [Fact]
    public void Test_CAS060_MirrorSyncError_CopyResultUnaffected()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        var baseOnGetTask = ApiHandler.OnGetTask!;
        ApiHandler.OnGetTask = (request, id) =>
        {
            if (id != CopyTaskId)
            {
                return baseOnGetTask(request, id);
            }

            ApiHandler.NewTotal = 9; // the copied row is Synced now
            return Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(CopyTaskId, "SyncCopy", "Completed")));
        };
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        CopyFirstNewRow(cut);

        cut.WaitForState(() => ToastText(cut).Contains("could not be refreshed automatically", StringComparison.Ordinal), PollWaitTimeout);
        PanelHeading.Count(cut, NewHeading).Should().Be("9");
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
    }

    [Fact]
    public void Test_CAS060_MirrorSyncUnauthorized_ShowsSessionExpired()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Completed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        CopyFirstNewRow(cut);

        cut.WaitForState(() => cut.Markup.Contains("Your session has expired", StringComparison.Ordinal), PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(1, "a 401 is not retried");
    }

    [Fact]
    public void Test_CAS060_MirrorSyncApiUnreachable_ShowsNonBlockingMessage()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Completed");
        ApiHandler.OnPostMirrorSync = _ => throw new HttpRequestException("simulated connection refused");

        CopyFirstNewRow(cut);

        cut.WaitForState(() => ToastText(cut).Contains("could not be refreshed automatically", StringComparison.Ordinal), PollWaitTimeout);
        cut.FindAll(".cachet-banner-error").Should().BeEmpty();
    }

    [Fact]
    public void Test_CAS060_MirrorSyncTimeout_ShowsNonBlockingMessage()
    {
        // An HttpClient timeout surfaces as TaskCanceledException without the page's token being
        // cancelled; it must be treated as "Api unreachable", not as a page close.
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Completed");
        ApiHandler.OnPostMirrorSync = _ => throw new TaskCanceledException("simulated HttpClient timeout");

        CopyFirstNewRow(cut);

        cut.WaitForState(() => ToastText(cut).Contains("could not be refreshed automatically", StringComparison.Ordinal), PollWaitTimeout);
    }

    [Fact]
    public void Test_CAS060_MirrorSyncAcceptedWithUnreadableBody_StillReportsStarted()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", CopyTaskId)));
        ActionTaskEndsWith(CopyTaskId, "SyncCopy", "Completed");
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json"),
        });

        CopyFirstNewRow(cut);

        cut.WaitForState(() => ToastText(cut) == "B2 mirror refresh started.", PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
    }

    [Fact]
    public void Test_CAS060_ResumedCopy_TriggersMirrorSync()
    {
        // The page opens while the user's Copy task is still Running (ResumePendingActionAsync).
        ApiHandler.UsePagedResults(removedTotal: 0, newTotal: 10, modifiedTotal: 0);
        var scan = TaskDetail(ScanTaskId, "ContentScan", "Completed");
        ApiHandler.OnGetTasks = request =>
        {
            var isCopyQuery = request.RequestUri!.Query.Contains("type=SyncCopy", StringComparison.OrdinalIgnoreCase);
            var item = isCopyQuery
                ? new TaskListItem { TaskId = CopyTaskId, Type = "SyncCopy", SubmittedBy = scan.SubmittedBy, SubmittedAt = scan.SubmittedAt, Status = "Running", IsAcknowledged = true }
                : new TaskListItem { TaskId = scan.TaskId, Type = scan.Type, SubmittedBy = scan.SubmittedBy, SubmittedAt = scan.SubmittedAt, StartTime = scan.StartTime, CompletedAt = scan.CompletedAt, Status = scan.Status, IsAcknowledged = true };
            var others = request.RequestUri!.Query.Contains("type=", StringComparison.OrdinalIgnoreCase)
                && !isCopyQuery
                && !request.RequestUri!.Query.Contains("type=ContentScan", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(Json(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = others ? Array.Empty<TaskListItem>() : new[] { item },
                Page = 1,
                PageSize = 1,
                TotalCount = others ? 0 : 1,
            }));
        };
        ApiHandler.OnGetTask = (_, id) => Task.FromResult(id switch
        {
            ScanTaskId => Json(HttpStatusCode.OK, scan),
            CopyTaskId => Json(HttpStatusCode.OK, TaskDetail(CopyTaskId, "SyncCopy", "Completed")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        ApiHandler.OnPostMirrorSync = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("B2MirrorSync", MirrorTaskId)));

        var cut = RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();

        cut.WaitForState(() => ToastText(cut).Contains($"Task #{MirrorTaskId}", StringComparison.Ordinal), PollWaitTimeout);
        ApiHandler.MirrorSyncCallCount.Should().Be(1);
    }
}
