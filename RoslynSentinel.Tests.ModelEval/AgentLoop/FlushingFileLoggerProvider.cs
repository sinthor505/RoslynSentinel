using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Tests.ModelEval.AgentLoop;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> that writes every log entry to a file and flushes after
/// each write. Exists because dotnet test's console logger block-buffers stdout when redirected to
/// a file (e.g. `dotnet test ... > out.txt`), so log lines written during a long-running model-eval
/// turn are invisible until the whole test process exits or a large internal buffer fills — this
/// sink bypasses that entirely by writing straight to disk with FileShare.ReadWrite so the file can
/// be tailed live while the test is still running.
/// </summary>
public sealed class FlushingFileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();

    public FlushingFileLoggerProvider(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FlushingFileLogger(this, categoryName);

    private void WriteLine(string categoryName, LogLevel level, string message, Exception? exception)
    {
        lock (_writeLock)
        {
            _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {categoryName}: {message}");
            if (exception is not null)
            {
                _writer.WriteLine(exception.ToString());
            }
        }
    }

    public void Dispose() => _writer.Dispose();

    private sealed class FlushingFileLogger(FlushingFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.WriteLine(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
