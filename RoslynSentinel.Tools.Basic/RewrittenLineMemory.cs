namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Process-wide memory of the lines the server itself rewrote in a file (a lifted rename, a RenameSymbol), so a later ReplaceSnippet whose
/// oldContent still holds the old text can be told what happened instead of getting a bare "oldContent was not found" (H3 R3 T3.1, H1 R4 T5,
/// G R3 T8: the model edited a call site a rename had just rewritten). Modelled on the rejected-edit memory in <see cref="WorkspaceFileEditImpl"/>:
/// one static instance because tool-class instances are per call; keyed by full file path; bounded by rows per file, file count and age; a file's
/// rows are dropped when a later edit writes it successfully. Only the lift and RenameSymbol feed it: no single hook point covers every
/// multi-file write, and those two are the writers whose output the model cannot see in its own edit.
/// </summary>
public sealed class RewrittenLineMemory
{
    private const int MaxRowsPerFile = 30;
    private const int MaxFiles = 50;
    private const int MaxNotesPerExplanation = 3;

    /// <summary>How long a rewrite is remembered; a rewrite older than this is unlikely to be what a model is still working from.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>The instance every tool-class instance shares.</summary>
    public static RewrittenLineMemory Shared { get; } = new();

    private readonly Dictionary<string, List<(string Source, int Line, string OldText, string NewText, DateTime At)>> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;

    /// <param name="utcNow">The clock, replaceable so a test can age entries; null uses the system clock.</param>
    public RewrittenLineMemory(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    private static string Key(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Remembers the rewritten lines of each file (full path to (1-based line, old trimmed text, new trimmed text)) as written by
    /// <paramref name="source"/> ("RenameSymbol" or "the rename lift"; it is quoted in the note). Call after the write succeeded and after the
    /// files' older rows were forgotten.
    /// </summary>
    public void Remember(string source, IReadOnlyDictionary<string, IReadOnlyList<(int Line, string OldText, string NewText)>> rewrites)
    {
        var now = _utcNow();
        lock (_gate)
        {
            Prune(now);
            foreach (var (path, rows) in rewrites)
            {
                var key = Key(path);
                if (!_byFile.TryGetValue(key, out var entries))
                {
                    _byFile[key] = entries = [];
                }

                entries.AddRange(rows
                    .Where(r => r.OldText.Length > 0 && r.NewText.Length > 0)
                    .Select(r => (source, r.Line, r.OldText, r.NewText, now)));
                if (entries.Count > MaxRowsPerFile)
                {
                    entries.RemoveRange(0, entries.Count - MaxRowsPerFile);
                }

                if (entries.Count == 0)
                {
                    _byFile.Remove(key);
                }
            }

            // Over the file cap: drop the files whose newest rewrite is oldest.
            while (_byFile.Count > MaxFiles)
            {
                _byFile.Remove(_byFile.OrderBy(kv => kv.Value.Max(e => e.At)).First().Key);
            }
        }
    }

    /// <summary>Drops the file's rows: a later edit wrote it, so the remembered lines no longer say what the file holds.</summary>
    public void Forget(string path)
    {
        lock (_gate)
        {
            _byFile.Remove(Key(path));
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var key in _byFile.Keys.ToList())
        {
            _byFile[key].RemoveAll(e => now - e.At > MaxAge);
            if (_byFile[key].Count == 0)
            {
                _byFile.Remove(key);
            }
        }
    }

    /// <summary>
    /// The note for a not-found <paramref name="oldContent"/>, or null when it does not look like old text this server rewrote: it holds the old
    /// text of a remembered rewritten line, the current <paramref name="fileText"/> no longer has that old line, and it does have the new line
    /// (so a row a later unhooked write made stale never produces a note). Says "&lt;file&gt;:&lt;line&gt; was rewritten by &lt;source&gt; earlier: it now
    /// reads '&lt;new line&gt;'" for up to three lines, then tells the model to re-read the file or use the new text.
    /// </summary>
    public string? Explain(string path, string oldContent, string fileText)
    {
        List<(string Source, int Line, string OldText, string NewText, DateTime At)> rows;
        var now = _utcNow();
        lock (_gate)
        {
            Prune(now);
            if (!_byFile.TryGetValue(Key(path), out var entries) || entries.Count == 0)
            {
                return null;
            }

            rows = [.. entries];
        }

        var normalizedOld = oldContent.Replace("\r\n", "\n").Replace('\r', '\n');
        var fileLines = new HashSet<string>(fileText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()), StringComparer.Ordinal);
        var fileName = Path.GetFileName(path);
        var notes = rows
            .Where(r => normalizedOld.Contains(r.OldText, StringComparison.Ordinal) && !fileLines.Contains(r.OldText) && fileLines.Contains(r.NewText))
            .Select(r => $"{fileName}:{r.Line} was rewritten by {r.Source} earlier: it now reads '{r.NewText}'.")
            .Distinct()
            .Take(MaxNotesPerExplanation)
            .ToList();
        return notes.Count == 0 ? null : string.Join(" ", notes) + " Re-read the file or use the new text.";
    }
}
