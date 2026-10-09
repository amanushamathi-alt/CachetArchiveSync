namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// CAS-054 §2.1/§9 V5 — contiguous resume-watermark tracker for one Keyword Search run. Candidates
/// are processed out of order by a bounded parallel loop, so a counter cannot say which ones are
/// done. This tracker works by position in the ascending-<c>file_index_id</c> candidate list: the
/// watermark is the <c>file_index_id</c> at the end of the longest fully-completed prefix, so a
/// still-running lower candidate always holds it below any completed higher one. When consecutive
/// candidates share a <c>file_index_id</c> (a Filename and a Content row of the same file) the
/// watermark never lands between them, because resume skips by <c>file_index_id &lt;= W</c>.
/// Thread-safe; the watermark never decreases.
/// </summary>
public sealed class ResumeWatermarkTracker
{
    private readonly object _gate = new();
    private readonly int[] _fileIndexIds;
    private readonly bool[] _completed;
    private int _prefixLength;
    private int? _watermark;

    /// <param name="orderedFileIndexIds">Candidate <c>file_index_id</c>s in ascending order, indexed by position.</param>
    /// <param name="initialWatermark">Watermark already inherited from an earlier run (the new floor), or null.</param>
    public ResumeWatermarkTracker(IReadOnlyList<int> orderedFileIndexIds, int? initialWatermark = null)
    {
        _fileIndexIds = orderedFileIndexIds.ToArray();
        _completed = new bool[_fileIndexIds.Length];
        _watermark = initialWatermark;
    }

    /// <summary>The current contiguous watermark, or null if none has been established.</summary>
    public int? Watermark
    {
        get
        {
            lock (_gate)
            {
                return _watermark;
            }
        }
    }

    /// <summary>
    /// Records that the candidate at <paramref name="position"/> is fully dispositioned and returns
    /// the (possibly advanced) watermark.
    /// </summary>
    public int? MarkCompleted(int position)
    {
        lock (_gate)
        {
            _completed[position] = true;

            var previousPrefix = _prefixLength;
            while (_prefixLength < _completed.Length && _completed[_prefixLength])
            {
                _prefixLength++;
            }

            if (_prefixLength > previousPrefix)
            {
                // Step back over a trailing run that shares the next candidate's file_index_id.
                var end = _prefixLength - 1;
                while (end >= 0
                    && end + 1 < _fileIndexIds.Length
                    && _fileIndexIds[end] == _fileIndexIds[end + 1])
                {
                    end--;
                }

                if (end >= 0 && (_watermark is null || _fileIndexIds[end] > _watermark))
                {
                    _watermark = _fileIndexIds[end];
                }
            }

            return _watermark;
        }
    }
}
