using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the long-entry check of the error-policy filter (<see cref="ErrorPolicyFilter.FindLongEntries"/> and
/// <see cref="ErrorPolicyFilter.WarnLongEntries"/>). Pure functions over a parsed document; no shared state.
/// </summary>
[TestFixture]
public class ErrorPolicyLongEntryWarningTests
{
    [Test]
    public void FindLongEntries_OneLongOneShort_ReturnsOnlyTheLongEntry()
    {
        string longBody = new('a', ErrorPolicyFilter.LongEntryThreshold + 1);
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(
            "### FileNotFound.Lookup\n" + longBody + "\n\n### FileNotFound.Edit\nShort edit body.\n");

        IReadOnlyList<(string Key, int Length)> result = ErrorPolicyFilter.FindLongEntries(doc);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Key, Is.EqualTo(ErrorPolicyKeys.ToWireName(ErrorPolicyKey.FileNotFoundLookup)));
        Assert.That(result[0].Length, Is.EqualTo(doc.Entries[ErrorPolicyKey.FileNotFoundLookup].Length));
    }

    [Test]
    public void FindLongEntries_OnlyShortEntries_ReturnsEmpty()
    {
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(
            "### FileNotFound.Lookup\nShort lookup body.\n\n### SymbolNotFound.Edit\nShort edit body.\n");

        IReadOnlyList<(string Key, int Length)> result = ErrorPolicyFilter.FindLongEntries(doc);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void FindLongEntries_NullDocument_ReturnsEmpty()
    {
        IReadOnlyList<(string Key, int Length)> result = ErrorPolicyFilter.FindLongEntries(null);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void WarnLongEntries_OneEntry_LogsExactlyOneWarningWithKeyAndLength()
    {
        var logger = new CapturingLogger();

        ErrorPolicyFilter.WarnLongEntries(new[] { ("FileNotFound.Lookup", 1234) }, logger);

        Assert.That(logger.Entries.Count, Is.EqualTo(1));
        Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(logger.Entries[0].Message, Does.Contain("FileNotFound.Lookup"));
        Assert.That(logger.Entries[0].Message, Does.Contain("1234"));
    }

    [Test]
    public void WarnLongEntries_EmptyListOrNullLogger_DoesNotThrowAndLogsNothing()
    {
        var logger = new CapturingLogger();

        Assert.DoesNotThrow(() => ErrorPolicyFilter.WarnLongEntries(Array.Empty<(string Key, int Length)>(), logger));
        Assert.That(logger.Entries, Is.Empty);

        Assert.DoesNotThrow(() => ErrorPolicyFilter.WarnLongEntries(new[] { ("FileNotFound.Lookup", 1234) }, null));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
