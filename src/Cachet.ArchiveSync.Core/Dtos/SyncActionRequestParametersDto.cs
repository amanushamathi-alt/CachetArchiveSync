namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Typed payload serialized into <c>dbo.task_queue.request_parameters</c> for
/// <c>task_type</c> values <c>SyncCopy</c>/<c>SyncUpdate</c>/<c>SyncPurge</c> (CAS-007 §6, per
/// CAS-004 §5's resolved action/result-catalog convention). <see cref="SourceTaskId"/> is the
/// originating <c>ContentScan</c> task; <see cref="FileIds"/> are the targeted
/// <c>sync_result_id</c> values. Serialized/deserialized with <c>System.Text.Json</c> at the
/// repository boundary only.
/// </summary>
public sealed record SyncActionRequestParametersDto
{
    public required int SourceTaskId { get; init; }

    public required IReadOnlyList<int> FileIds { get; init; }
}
