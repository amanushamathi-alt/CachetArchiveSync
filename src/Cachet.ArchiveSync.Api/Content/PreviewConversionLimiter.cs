using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// CAS-040 §9/§12 — bounds preview conversions with a process-wide <see cref="SemaphoreSlim"/>
/// (<c>MaxConcurrentConversions</c>) and a wall-clock timeout (<c>TimeoutSeconds</c>). NPOI work
/// cannot be cancelled mid-parse: after a timeout the caller gets <see cref="Status.Timeout"/>
/// while the background parse runs to completion, still holding its semaphore slot. Registered as
/// a singleton so the semaphore is genuinely per-process.
/// </summary>
public sealed class PreviewConversionLimiter : IDisposable
{
    public enum Status
    {
        Completed,
        Timeout,
        Failed
    }

    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _timeout;

    public PreviewConversionLimiter(IOptions<FilePreviewOptions> options)
    {
        _semaphore = new SemaphoreSlim(options.Value.MaxConcurrentConversions, options.Value.MaxConcurrentConversions);
        _timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool. Waiting for a slot counts toward the
    /// timeout. Any exception from <paramref name="work"/> is reported as
    /// <see cref="Status.Failed"/> (V9 <c>ConversionFailed</c>); its message is not surfaced, since
    /// it could echo document content (§11).
    /// </summary>
    public async Task<(Status Status, T? Value)> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        if (!await _semaphore.WaitAsync(_timeout, ct))
        {
            return (Status.Timeout, default);
        }

        Task<T> task;
        try
        {
            task = Task.Run(work, CancellationToken.None);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }

        // The slot is released only when the work itself finishes, not when the caller stops
        // waiting — this is what keeps timed-out parses bounded (§12).
        _ = task.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // observe, so a late failure is never an unobserved task exception
                try
                {
                    ((SemaphoreSlim)state!).Release();
                }
                catch (ObjectDisposedException)
                {
                    // Host shutting down — nothing left to bound.
                }
            },
            _semaphore,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var remaining = _timeout - (DateTime.UtcNow - started);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        try
        {
            var value = await task.WaitAsync(remaining, ct);
            return (Status.Completed, value);
        }
        catch (TimeoutException)
        {
            return (Status.Timeout, default);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (Status.Failed, default);
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
