using System.ComponentModel.DataAnnotations;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// CAS-028 §9 — Worker queue-harness tuning values, bound from the <c>Worker</c> configuration
/// section and validated on start (§3.6, V1/V2). Tuning values only, never credentials (FR-03).
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Delay between claim attempts when nothing is queued; also the startup-sweep and
    /// instance-lock retry delay (design §5.1). V1: 1–60.
    /// </summary>
    [Range(1, 60)]
    public int PollingIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum tasks executing concurrently in this Worker process (design §5.3). V2: 1–16.
    /// Not load-validated — WBS Task 36.
    /// </summary>
    [Range(1, 16)]
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// CAS-029 §3.4/§9 — minimum seconds between intermediate progress writes per task
    /// (<see cref="TaskProgressReporter"/>). V1: 5–300. Not BRD-sourced (CAS-029 §15 OQ-3).
    /// </summary>
    [Range(5, 300)]
    public int ProgressReportIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// CAS-054/CAS-055 — a Keyword Search is auto-resumed only if its last progress write is within
    /// this many hours. Same key the Api reads (<c>Worker:ResumeMaxAgeHours</c>). 1–720.
    /// </summary>
    [Range(1, 720)]
    public int ResumeMaxAgeHours { get; set; } = 24;

    /// <summary>
    /// CAS-055 §2.4 — consecutive automatic resumes allowed per task lineage (crash-loop guard).
    /// 0–10; 0 disables auto-resume.
    /// </summary>
    [Range(0, 10)]
    public int AutoResumeMaxAttempts { get; set; } = 3;

    /// <summary>
    /// CAS-056 §2.2 — seconds between polls of <c>task_queue.is_cancel_requested</c> for a running
    /// Keyword Search (<see cref="CancelRequestWatcher"/>). V13: 1–60.
    /// </summary>
    [Range(1, 60)]
    public int CancelPollIntervalSeconds { get; set; } = 2;
}
