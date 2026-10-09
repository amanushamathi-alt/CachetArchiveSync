using System.ComponentModel.DataAnnotations;

namespace Cachet.ArchiveSync.Web;

/// <summary>
/// CAS-030 §8.4/§9 — the single poll interval shared by the Task Manager badge and every
/// in-progress screen poll loop (design §8, §13). Bound from the <c>Worker</c> section: the key
/// name <c>Worker:TaskManagerPollIntervalSeconds</c> is taken verbatim from design §13 even though
/// the Web process reads it (§15 Q5). Validated on start (V8). Holds no credential (FR-03).
/// </summary>
public sealed class TaskPollingOptions
{
    public const string SectionName = "Worker";

    /// <summary>Seconds between poll ticks. V8: integer 5–60. Default 10 (design §8).</summary>
    [Range(5, 60)]
    public int TaskManagerPollIntervalSeconds { get; set; } = 10;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(TaskManagerPollIntervalSeconds);

    /// <summary>
    /// CAS-060: how many times the O-Drive Sync screen tries <c>POST /sync/mirror-sync</c> after a
    /// completed Copy while it keeps getting 409 (a sync already Queued/Running), one try per poll
    /// interval. Default 30 = about 5 minutes at the default 10 s interval.
    /// </summary>
    [Range(1, 360)]
    public int MirrorSyncMaxAttempts { get; set; } = 30;
}
