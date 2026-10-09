using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// CAS-029 §3.4 — scoped factory that creates one <see cref="TaskProgressReporter"/> per
/// executing task, over the scope's <see cref="ITaskQueueRepository"/>, the registered
/// <see cref="TimeProvider"/>, and <c>Worker:ProgressReportIntervalSeconds</c> (§9).
/// </summary>
public sealed class TaskProgressReporterFactory
{
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly ILogger<TaskProgressReporter> _logger;

    public TaskProgressReporterFactory(
        ITaskQueueRepository taskQueueRepository,
        TimeProvider timeProvider,
        IOptions<WorkerOptions> options,
        ILogger<TaskProgressReporter> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _timeProvider = timeProvider;
        _interval = TimeSpan.FromSeconds(options.Value.ProgressReportIntervalSeconds);
        _logger = logger;
    }

    public TaskProgressReporter Create(int taskQueueId) =>
        new(taskQueueId, _taskQueueRepository, _timeProvider, _interval, _logger);
}
