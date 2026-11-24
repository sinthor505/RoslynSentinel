using System.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// Detects a model stuck repeating one sentence in a streamed text channel. Fed the channel's text deltas, it cuts the
/// stream into sentences (at ". ", "! ", "? " and newline), normalizes each (trimmed, lower-cased, whitespace collapsed,
/// trailing sentence punctuation dropped), ignores sentences shorter than <see cref="MinSentenceLength"/> characters and
/// any text inside a triple-backtick code fence, and counts occurrences over the last <see cref="WindowSentences"/>
/// counted sentences. When one sentence reaches <see cref="RepeatLimit"/> occurrences, <see cref="Append"/> returns it.
/// One instance per channel (content, reasoning); not thread-safe. No MCP types: engine-style logic.
/// </summary>
internal sealed class StreamPhraseLoopDetector
{
    /// <summary>Occurrences of one normalized sentence within the window that count as a loop (K).</summary>
    internal const int RepeatLimit = 20;

    /// <summary>Normalized sentences shorter than this are ignored (stops "}" lines, "ok." and similar from counting).</summary>
    internal const int MinSentenceLength = 15;

    /// <summary>How many of the most recent counted sentences the occurrence counts cover.</summary>
    internal const int WindowSentences = 200;

    /// <summary>An unfinished sentence longer than this is dropped (a very long single line is not a phrase loop); bounds the rescanned buffer.</summary>
    private const int MaxPendingChars = 4096;

    private const string CodeFence = "```";

    private readonly StringBuilder _pending = new();
    private readonly Queue<string> _window = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private bool _inCodeFence;

    /// <summary>
    /// Feeds one text delta. Returns the normalized sentence that reached <see cref="RepeatLimit"/> occurrences, or null
    /// while no sentence has. A delta may end mid-sentence: the unfinished tail is buffered until its boundary arrives.
    /// </summary>
    public string? Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return null;
        }

        _pending.Append(delta);
        var text = _pending.ToString();
        var segmentStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            int boundaryEnd;
            if (c == '\n')
            {
                boundaryEnd = i + 1;
            }
            else if (c is '.' or '!' or '?')
            {
                if (i + 1 >= text.Length)
                {
                    // The delimiter is the space after the punctuation; it may arrive in the next delta.
                    break;
                }

                if (text[i + 1] != ' ')
                {
                    i++;
                    continue;
                }

                boundaryEnd = i + 2;
            }
            else
            {
                i++;
                continue;
            }

            var segment = text[segmentStart..boundaryEnd];
            segmentStart = boundaryEnd;
            i = boundaryEnd;
            if (Record(segment) is { } looped)
            {
                return looped;
            }
        }

        _pending.Clear();
        var tail = text[segmentStart..];
        if (tail.Length > MaxPendingChars)
        {
            // Keep the code-fence state correct even though the text itself is dropped.
            CountFencesAndToggle(tail);
        }
        else
        {
            _pending.Append(tail);
        }

        return null;
    }

    private string? Record(string segment)
    {
        var wasInFence = _inCodeFence;
        var fenceCount = CountFencesAndToggle(segment);
        if (wasInFence || fenceCount > 0)
        {
            return null;
        }

        var normalized = Normalize(segment);
        if (normalized.Length < MinSentenceLength)
        {
            return null;
        }

        _window.Enqueue(normalized);
        _counts[normalized] = _counts.GetValueOrDefault(normalized) + 1;
        if (_window.Count > WindowSentences)
        {
            var evicted = _window.Dequeue();
            if (--_counts[evicted] == 0)
            {
                _counts.Remove(evicted);
            }
        }

        return _counts.GetValueOrDefault(normalized) >= RepeatLimit ? normalized : null;
    }

    private int CountFencesAndToggle(string text)
    {
        var count = 0;
        var index = text.IndexOf(CodeFence, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            _inCodeFence = !_inCodeFence;
            index = text.IndexOf(CodeFence, index + CodeFence.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string Normalize(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        var pendingSpace = false;
        foreach (var ch in segment)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().TrimEnd('.', '!', '?', ' ');
    }
}
