using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Cachet.ArchiveSync.Worker.Extraction;

internal enum ExtractorHostOutcome
{
    NoMatch,
    Match,
    Encrypted,
    Error,

    /// <summary>The child process died mid-request (stack overflow, native crash, out of memory, ...).</summary>
    Crashed,

    /// <summary>The child exceeded the time limit and was killed.</summary>
    TimedOut
}

internal sealed record ExtractorHostResult(ExtractorHostOutcome Outcome, string? Text = null, int? ExitCode = null);

/// <summary>
/// A bounded pool of long-lived <see cref="ExtractorHost"/> child processes. One request runs per
/// child at a time; a child that crashes, times out or is cancelled is discarded and a fresh one is
/// started on demand, so a poisonous file can only ever cost one child and one result.
/// </summary>
internal sealed class ExtractorHostPool : IDisposable
{
    private readonly Func<ProcessStartInfo> _startInfoFactory;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<ExtractorHostProcess> _idle = new();
    private volatile bool _disposed;

    // A child is replaced after this many requests so parser heap growth/fragmentation never accumulates.
    private const int MaxRequestsPerHost = 100;

    // Each child holds a whole file plus parser structures (often 5-10x the file size), so the default
    // is deliberately NOT one per core: on a many-core server that exhausts commit memory.
    private const int DefaultContentConcurrency = 4;
    private const int DefaultExtractorMemoryLimitMb = 1024;

    /// <summary>Content-match concurrency (and pool size): <c>Worker:ContentMatchMaxConcurrency</c>, default min(cores, 4).</summary>
    internal static int ResolveConcurrency(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue<int?>("Worker:ContentMatchMaxConcurrency")
            ?? Math.Min(Environment.ProcessorCount, DefaultContentConcurrency));

    // Public constructor used by DI: pool size matches the content-match concurrency.
    public ExtractorHostPool(IConfiguration configuration)
        : this(
            () => DefaultStartInfo(Math.Max(64, configuration.GetValue<int?>("Worker:ExtractorMemoryLimitMb") ?? DefaultExtractorMemoryLimitMb)),
            ResolveConcurrency(configuration))
    {
    }

    internal ExtractorHostPool(Func<ProcessStartInfo> startInfoFactory, int size)
    {
        _startInfoFactory = startInfoFactory;
        _slots = new SemaphoreSlim(size, size);
    }

    public async Task<ExtractorHostResult> ExtractAsync(
        byte[] data, string extension, string term, TimeSpan timeout, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        ExtractorHostProcess? host = null;
        var reusable = false;
        try
        {
            host = TakeHealthyIdle() ?? ExtractorHostProcess.Start(_startInfoFactory());
            var result = await host.ExtractAsync(data, extension, term, timeout, ct);
            reusable = result.Outcome is not (ExtractorHostOutcome.Crashed or ExtractorHostOutcome.TimedOut)
                && host.RequestCount < MaxRequestsPerHost;
            return result;
        }
        finally
        {
            if (host is not null)
            {
                if (reusable && !_disposed)
                {
                    _idle.Add(host);
                }
                else
                {
                    host.Dispose();
                }
            }

            _slots.Release();
        }
    }

    private ExtractorHostProcess? TakeHealthyIdle()
    {
        while (_idle.TryTake(out var candidate))
        {
            if (candidate.IsAlive)
            {
                return candidate;
            }

            candidate.Dispose();
        }

        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryTake(out var host))
        {
            host.Dispose();
        }

        _slots.Dispose();
    }

    private static ProcessStartInfo DefaultStartInfo(int memoryLimitMb)
    {
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the Worker executable path.");
        var info = new ProcessStartInfo(path);

        // Cap the child's GC heap: a pathological file then raises a catchable OutOfMemoryException
        // (reported as an extraction Error, child survives) instead of starving the whole server.
        // The runtime reads this variable as hexadecimal bytes.
        info.Environment["DOTNET_GCHeapHardLimit"] = ((long)memoryLimitMb * 1024 * 1024).ToString("X");
        // Don't inherit the service's working directory (C:\WINDOWS\system32).
        info.WorkingDirectory = AppContext.BaseDirectory;

        // Running as `dotnet Worker.dll` (development): re-launch through dotnet with the same dll.
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Cannot determine the Worker assembly path."));
        }

        info.ArgumentList.Add(ExtractorHost.Argument);
        return info;
    }
}

/// <summary>One child process and the framed request/response conversation with it.</summary>
internal sealed class ExtractorHostProcess : IDisposable
{
    private const int MaxStderrChars = 2000;

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly StringBuilder _stderr = new();

    private ExtractorHostProcess(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
    }

    /// <summary>Requests this child has served; the pool retires it after a fixed number.</summary>
    public int RequestCount { get; private set; }

    public bool IsAlive
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static ExtractorHostProcess Start(ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        var process = new Process { StartInfo = info };
        var host = new ExtractorHostProcess(process.Start() ? process : throw new InvalidOperationException("Extractor process did not start."));

        // Keep a short excerpt of the child's stderr: for a crash it carries the reason
        // ("Stack overflow", an unhandled exception) and is logged with the skipped file.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (host._stderr)
                {
                    if (host._stderr.Length < MaxStderrChars)
                    {
                        host._stderr.Append(e.Data, 0, Math.Min(e.Data.Length, MaxStderrChars - host._stderr.Length)).Append(' ');
                    }
                }
            }
        };
        process.BeginErrorReadLine();
        return host;
    }

    public async Task<ExtractorHostResult> ExtractAsync(
        byte[] data, string extension, string term, TimeSpan timeout, CancellationToken ct)
    {
        RequestCount++;
        var work = RoundTripAsync(data, extension, term);
        var winner = await Task.WhenAny(work, Task.Delay(timeout, ct));

        if (winner != work)
        {
            // Time limit hit (or the caller cancelled): kill the child, which also unblocks the pending
            // read/write, then observe the abandoned task so it never surfaces as unobserved.
            Kill();
            await work.ContinueWith(_ => { }, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
            return new ExtractorHostResult(ExtractorHostOutcome.TimedOut);
        }

        return await work;
    }

    private async Task<ExtractorHostResult> RoundTripAsync(byte[] data, string extension, string term)
    {
        try
        {
            using (var request = new MemoryStream())
            using (var writer = new BinaryWriter(request, Encoding.UTF8, leaveOpen: true))
            {
                ExtractorHost.WriteBlock(writer, Encoding.UTF8.GetBytes(extension));
                ExtractorHost.WriteBlock(writer, Encoding.UTF8.GetBytes(term));
                writer.Write(data.Length);
                writer.Flush();
                request.Position = 0;
                await request.CopyToAsync(_stdin);
            }

            await _stdin.WriteAsync(data);
            await _stdin.FlushAsync();

            var status = new byte[1];
            await ReadExactAsync(status);
            var lengthBytes = new byte[4];
            await ReadExactAsync(lengthBytes);
            var payload = new byte[BitConverter.ToInt32(lengthBytes)];
            await ReadExactAsync(payload);
            var text = Encoding.UTF8.GetString(payload);

            return status[0] switch
            {
                ExtractorHost.Match => new ExtractorHostResult(ExtractorHostOutcome.Match, text),
                ExtractorHost.Encrypted => new ExtractorHostResult(ExtractorHostOutcome.Encrypted),
                ExtractorHost.Error => new ExtractorHostResult(ExtractorHostOutcome.Error, text),
                _ => new ExtractorHostResult(ExtractorHostOutcome.NoMatch)
            };
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or InvalidOperationException)
        {
            // Broken pipe / EOF: the child died. Report how, so the skipped file's log line carries it.
            int? exitCode = null;
            try
            {
                if (_process.WaitForExit(2000))
                {
                    exitCode = _process.ExitCode;
                }
            }
            catch (InvalidOperationException)
            {
            }

            string stderr;
            lock (_stderr)
            {
                stderr = _stderr.ToString().Trim();
            }

            return new ExtractorHostResult(ExtractorHostOutcome.Crashed, stderr, exitCode);
        }
    }

    private async Task ReadExactAsync(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _stdout.ReadAsync(buffer.AsMemory(read));
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
