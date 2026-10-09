using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cachet.ArchiveSync.Web.Tests.Auth;

/// <summary>
/// CAS-021 SC-7: captures every formatted log message (and exception text) written anywhere in the
/// test host, so <see cref="WebAuthFlowIntegrationTests.Test_SC7_NoCredentialLeakageInLoginUiSurface"/>
/// can assert the plaintext password never reaches "Web's own log output" — not just that
/// <c>Login.razor</c>'s own two <c>LogInformation</c>/<c>LogWarning</c> calls look safe by
/// inspection, but that nothing else on the request path logged it either.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _messages;

        public CapturingLogger(string category, ConcurrentQueue<string> messages)
        {
            _category = category;
            _messages = messages;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _messages.Enqueue(
                $"[{_category}] {message}" + (exception is not null ? $" | {exception}" : string.Empty));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
