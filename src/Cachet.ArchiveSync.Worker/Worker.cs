using System.Collections.Concurrent;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// Durable, bounded-parallel task-queue harness (CAS-028 §3.3–§3.7; claim-loop skeleton first
/// built by CAS-006 §3). Order on start: acquire the single-instance lock (§3.7 — a standby
/// instance never sweeps or claims) → startup sweep of <c>Running</c> rows orphaned by a previous
/// process lifetime (§3.4 — must succeed before any claim) → claim loop with up to
/// <see cref="WorkerOptions.MaxDegreeOfParallelism"/> tasks executing at once, each in its own DI
/// scope, dispatched to whichever registered <see cref="IWorkerTaskProcessor"/> matches its
/// <c>TaskType</c>. The dispatch boundary (§3.5) never leaves a task <c>Running</c> except on
/// shutdown cancellation or a failed audit write — both resolved by the next startup sweep.
/// </summary>
public sealed class Worker : BackgroundService
{
    private const string ActingProcessIdentity = "worker-svc";

    /// <summary>CAS-028 §3.4 — fixed sweep failure reason, passed to <c>dbo.task_queue_sweep_interrupted</c>.</summary>
    internal const string InterruptedByRestartReason =
        "Interrupted by a service restart - please restart or resubmit this task.";

    /// <summary>CAS-028 §3.5 — fixed reason when no processor is registered for the claimed <c>task_type</c>.</summary>
    internal const string UnsupportedTaskTypeReason = "This task type is not supported by the worker.";

    /// <summary>CAS-028 §3.5 — fixed reason when a non-cancellation exception escapes a processor.</summary>
    internal const string UnexpectedErrorReason = "Unexpected error while processing this task.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkerInstanceLock _instanceLock;
    private readonly WorkerOptions _options;
    private readonly TimeSpan _pollingInterval;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IServiceScopeFactory scopeFactory,
        IWorkerInstanceLock instanceLock,
        IOptions<WorkerOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory;
        _instanceLock = instanceLock;
        _options = options.Value;
        _pollingInterval = TimeSpan.FromSeconds(_options.PollingIntervalSeconds);
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker started: MaxDegreeOfParallelism={Parallelism}, PollingIntervalSeconds={Poll}",
            _options.MaxDegreeOfParallelism, _options.PollingIntervalSeconds);

        try
        {
            await AcquireInstanceLockAsync(stoppingToken);
            await RunStartupSweepAsync(stoppingToken);
            await RunClaimLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host stopping — in-flight tasks were awaited by RunClaimLoopAsync (§3.4 graceful shutdown).
        }
    }

    /// <summary>§3.7 — stand by until this instance holds the lock. Never sweeps or claims before then (V7).</summary>
    private async Task AcquireInstanceLockAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                if (await _instanceLock.TryAcquireAsync(stoppingToken))
                {
                    return;
                }

                _logger.LogWarning(
                    "Another Worker instance is active; standing by — retrying in {Delay}s",
                    _options.PollingIntervalSeconds);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "Worker instance lock acquisition failed; retrying in {Delay}s — no tasks will be swept or claimed until it is acquired",
                    _options.PollingIntervalSeconds);
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }
    }

    /// <summary>§3.4 — sweep orphaned <c>Running</c> rows; retried until it succeeds (V3).</summary>
    private async Task RunStartupSweepAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var taskQueueRepository = scope.ServiceProvider.GetRequiredService<ITaskQueueRepository>();

                var swept = await taskQueueRepository.SweepInterruptedAsync(
                    ActingProcessIdentity, InterruptedByRestartReason, stoppingToken,
                    _options.ResumeMaxAgeHours,
                    _options.AutoResumeMaxAttempts);

                if (swept.Count > 0)
                {
                    _logger.LogWarning(
                        "Startup sweep failed {Count} interrupted task(s): {TaskIds}",
                        swept.Count, string.Join(", ", swept.Select(s => s.TaskQueueId)));

                    // CAS-055 §2.7 — one warning per auto-resumed / suppressed task id; ids only.
                    foreach (var task in swept)
                    {
                        if (task.ResumedAsTaskQueueId is { } resumedId)
                        {
                            _logger.LogWarning(
                                "Startup sweep auto-resumed interrupted task {TaskId} as task {ResumedTaskId}",
                                task.TaskQueueId, resumedId);
                        }
                        else if (task.AutoResumeSuppressed)
                        {
                            _logger.LogWarning(
                                "Startup sweep did not auto-resume interrupted task {TaskId}: attempt cap reached - manual restart required",
                                task.TaskQueueId);
                        }
                    }
                }

                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "Startup sweep failed; retrying in {Delay}s — no tasks will be claimed until it succeeds",
                    _options.PollingIntervalSeconds);
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }
    }

    /// <summary>§3.3 — bounded-parallel claim loop. A slot is always released, whatever the outcome.</summary>
    private async Task RunClaimLoopAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(_options.MaxDegreeOfParallelism, _options.MaxDegreeOfParallelism);
        var inFlight = new ConcurrentDictionary<Task, byte>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);

                // §3.7 / V8 — the held check runs before every claim. False (or a throw) while not
                // stopping means the lock was lost: stop claiming and stop the host.
                bool held;
                try
                {
                    held = await _instanceLock.IsHeldAsync(stoppingToken);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    held = false;
                }

                if (!held)
                {
                    slots.Release();
                    stoppingToken.ThrowIfCancellationRequested();
                    _logger.LogCritical(
                        "Worker instance lock lost; no further tasks will be claimed — stopping the host");
                    _lifetime.StopApplication();
                    break;
                }

                ClaimedTaskRow? claimed;
                try
                {
                    // Own DI scope for the claim (§3.3). READPAST/UPDLOCK claim — safe under
                    // concurrent slots and processes (§3.2).
                    await using var claimScope = _scopeFactory.CreateAsyncScope();
                    var taskQueueRepository = claimScope.ServiceProvider.GetRequiredService<ITaskQueueRepository>();
                    claimed = await taskQueueRepository.ClaimNextAsync(ActingProcessIdentity, stoppingToken);
                }
                catch (Exception ex)
                {
                    slots.Release();
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    _logger.LogError(ex,
                        "Unhandled error while claiming the next task — will retry after {Delay}s",
                        _options.PollingIntervalSeconds);
                    await Task.Delay(_pollingInterval, stoppingToken);
                    continue;
                }

                if (claimed is null)
                {
                    slots.Release();
                    await Task.Delay(_pollingInterval, stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "Claimed task {TaskId} ({TaskType}); {InFlight}/{Parallelism} slots busy",
                    claimed.TaskQueueId, claimed.TaskType,
                    _options.MaxDegreeOfParallelism - slots.CurrentCount, _options.MaxDegreeOfParallelism);

                // Not awaited — the loop claims again immediately if another slot is free (§3.3).
                // RunInSlotAsync releases the slot in its own finally and never throws.
                var slotTask = Task.Run(() => RunInSlotAsync(claimed, slots, stoppingToken), CancellationToken.None);
                inFlight.TryAdd(slotTask, 0);
                _ = slotTask.ContinueWith(
                    t => inFlight.TryRemove(t, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            // §3.4 / SC-12 — let in-flight tasks observe cancellation so the process never exits mid-write.
            await Task.WhenAll(inFlight.Keys);
        }
    }

    /// <summary>§3.5 — dispatch boundary for one claimed task, in its own DI scope.</summary>
    private async Task RunInSlotAsync(ClaimedTaskRow claimed, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            try
            {
                var processor = scope.ServiceProvider
                    .GetServices<IWorkerTaskProcessor>()
                    .FirstOrDefault(p => p.TaskType == claimed.TaskType);

                if (processor is null)
                {
                    _logger.LogWarning(
                        "No IWorkerTaskProcessor registered for task_type {TaskType} (task {TaskId}) — failing it at the dispatch boundary",
                        claimed.TaskType, claimed.TaskQueueId);
                    await FailAtBoundaryAsync(scope.ServiceProvider, claimed, UnsupportedTaskTypeReason, stoppingToken);
                    return;
                }

                await processor.ProcessAsync(claimed, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // §3.4 — shutdown: deliberately no status write; the next startup sweep resolves it.
            }
            catch (Exception ex)
            {
                // Exception goes to ILogger only — never to result_detail or audit_log (§3.5, §11).
                _logger.LogError(ex, "Task {TaskId} ({TaskType}) failed at the dispatch boundary",
                    claimed.TaskQueueId, claimed.TaskType);
                await FailAtBoundaryAsync(scope.ServiceProvider, claimed, UnexpectedErrorReason, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during the boundary's own writes — left Running for the next startup sweep.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled error in the execution slot for task {TaskId} ({TaskType}); leaving Running for the next startup sweep",
                claimed.TaskQueueId, claimed.TaskType);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// §3.5 — audit <c>TaskFailed</c> FIRST, then write <c>Failed</c>. Fail-closed: if the audit
    /// write throws, no status is written (FR-23 — a terminal status is never visible without its
    /// audit record); the row stays <c>Running</c> for the next startup sweep.
    /// </summary>
    private async Task FailAtBoundaryAsync(
        IServiceProvider services, ClaimedTaskRow claimed, string failureReason, CancellationToken ct)
    {
        try
        {
            var auditLogService = services.GetRequiredService<IAuditLogService>();
            await auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = claimed.SubmittedByUserId,
                TabModule = WorkerTaskModuleMap.ToTabModule(claimed.TaskType),
                Action = AuditAction.TaskFailed,
                TaskQueueId = claimed.TaskQueueId,
                Result = AuditResult.Failure,
                ResultDetail = failureReason
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Audit write failed for task {TaskId}; leaving Running for the next startup sweep",
                claimed.TaskQueueId);
            return;
        }

        try
        {
            var taskQueueRepository = services.GetRequiredService<ITaskQueueRepository>();
            await taskQueueRepository.UpdateStatusAsync(
                claimed.TaskQueueId,
                ActingProcessIdentity,
                "Failed",
                JsonSerializer.Serialize(new { FailureReason = failureReason }),
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Status write failed for task {TaskId} after its TaskFailed audit; the task shows Running until the next startup sweep",
                claimed.TaskQueueId);
        }
    }
}

/// <summary>
/// CAS-028 §3.5 — <c>task_type</c> → <c>audit_log.tab_module</c> mapping for Worker-originated
/// audit rows. Must agree with <c>dbo.task_queue_sweep_interrupted</c>'s
/// <c>CASE task_type WHEN N'KeywordSearch' THEN N'Keyword Search' ELSE N'O-Drive Sync' END</c>;
/// never <c>Task Manager</c> (design §9.3).
/// </summary>
internal static class WorkerTaskModuleMap
{
    internal const string KeywordSearchModule = "Keyword Search";
    internal const string ODriveSyncModule = "O-Drive Sync";

    // ContentScan, SyncCopy, SyncUpdate, SyncPurge → O-Drive Sync (the SP's ELSE branch).
    internal static string ToTabModule(string taskType) =>
        taskType == "KeywordSearch" ? KeywordSearchModule : ODriveSyncModule;
}
