using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-056 §13 "CancelRequestWatcherTests": the per-task poller, driven by a <see cref="FakeTimeProvider"/> (no real
/// sleeps; waits are gates). Methods are not spec-mapped items (the map assigns the watcher to V6, proven end to end in
/// <see cref="KeywordSearchCancelTests"/>), so they are named <c>Cov_*</c>.
/// </summary>
public sealed class CancelRequestWatcherTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    private readonly ITaskQueueRepository _repository = Substitute.For<ITaskQueueRepository>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly FakeTimeProvider _time = new();
    private readonly SemaphoreSlim _polled = new(0);

    private CancelRequestWatcher Create() => new(100, _repository, _time, Interval, _logger);

    private static Task WhenCancelledAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => tcs.TrySetResult());
        return tcs.Task.WaitAsync(Safety);
    }

    [Fact]
    public async Task Cov_PollsAtInterval_FiresTokenOnce_ThenStopsPolling()
    {
        // Arrange - not requested, then requested.
        var answers = new Queue<bool>(new[] { false, true });
        _repository.IsCancelRequestedAsync(100, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var answer = answers.Dequeue();
            _polled.Release();
            return Task.FromResult(answer);
        });
        var watcher = Create();

        // Act / Assert - nothing before one full interval.
        _time.Advance(TimeSpan.FromSeconds(1));
        await _repository.DidNotReceive().IsCancelRequestedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        await _polled.WaitAsync(Safety);
        watcher.Token.IsCancellationRequested.Should().BeFalse("the first poll saw no request");

        _time.Advance(Interval);
        await _polled.WaitAsync(Safety);
        await WhenCancelledAsync(watcher.Token);

        // Polling stops once the flag has been seen: further ticks do no work.
        _time.Advance(TimeSpan.FromSeconds(30));
        watcher.Dispose();
        await _repository.Received(2).IsCancelRequestedAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Cov_DisposeStopsPolling_WithoutFiringToken()
    {
        // Arrange
        var watcher = Create();

        // Act
        watcher.Dispose();
        watcher.Dispose(); // idempotent
        _time.Advance(TimeSpan.FromSeconds(30));

        // Assert
        _repository.DidNotReceive().IsCancelRequestedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cov_PollFailure_IsLoggedWithTaskIdOnly_AndRetriedNextTick()
    {
        // Arrange - first read throws (flaky DB), second says cancelled.
        var calls = 0;
        _repository.IsCancelRequestedAsync(100, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var n = ++calls;
            _polled.Release();
            return n == 1
                ? Task.FromException<bool>(new InvalidOperationException("db unavailable"))
                : Task.FromResult(true);
        });
        var watcher = Create();

        // Act
        _time.Advance(Interval);
        await _polled.WaitAsync(Safety);
        _time.Advance(Interval);
        await _polled.WaitAsync(Safety);
        await WhenCancelledAsync(watcher.Token);
        watcher.Dispose();

        // Assert - the failure did not stop the watcher and was logged as a warning.
        calls.Should().Be(2);
        _logger.ReceivedCalls().Select(c => c.GetArguments())
            .Should().Contain(a => a.Length > 0 && a[0] is LogLevel && (LogLevel)a[0]! == LogLevel.Warning);
    }
}

/// <summary>CAS-056 V13 - <c>Worker:CancelPollIntervalSeconds</c> is range-checked 1-60 at startup.</summary>
public sealed class WorkerOptionsCancelPollTests
{
    private static WorkerOptions Resolve(IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<WorkerOptions>()
            .Bind(configuration.GetSection(WorkerOptions.SectionName))
            .ValidateDataAnnotations();
        return services.BuildServiceProvider().GetRequiredService<IOptions<WorkerOptions>>().Value;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(61, false)]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(null, true)]
    public void Test_V13_CancelPollIntervalIsRangeChecked(int? value, bool valid)
    {
        // Arrange - null means the key is absent, so the default (2) applies.
        var values = new Dictionary<string, string?>();
        if (value is not null)
        {
            values["Worker:CancelPollIntervalSeconds"] = value.Value.ToString();
        }

        // Act
        var act = () => Resolve(values);

        // Assert
        if (valid)
        {
            act.Should().NotThrow().Which.CancelPollIntervalSeconds.Should().Be(value ?? 2);
        }
        else
        {
            act.Should().Throw<OptionsValidationException>()
                .Which.Failures.Should().ContainSingle(f => f.Contains(nameof(WorkerOptions.CancelPollIntervalSeconds)));
        }
    }
}
