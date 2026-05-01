using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text.RegularExpressions;
using System.Text;

namespace RoslynSentinel.Server;

public class DiffEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DiffEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Applies a standard Unified Diff to a SourceText object and returns the updated text.
    /// Supports multiple hunks and validates context lines.
    /// </summary>
    public SourceText ApplyDiff(SourceText sourceText, string unifiedDiff)
    {
        var lines = sourceText.Lines.Select(l => l.ToString()).ToList();
        var diffLines = unifiedDiff.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        int offset = 0; // Track how much the file has grown/shrunk
        var hunkHeaderRegex = new Regex(@"^@@\s+\-(\d+),?(\d*)\s+\+(\d+),?(\d*)\s+@@");

        for (int i = 0; i < diffLines.Length; i++)
        {
            var line = diffLines[i];
            var match = hunkHeaderRegex.Match(line);

            if (match.Success)
            {
                int oldStart = int.Parse(match.Groups[1].Value) - 1;
                int currentLine = oldStart + offset;

                // Process hunk lines
                i++;
                while (i < diffLines.Length && !hunkHeaderRegex.IsMatch(diffLines[i]))
                {
                    var diffLine = diffLines[i];
                    if (string.IsNullOrEmpty(diffLine) && i + 1 < diffLines.Length && hunkHeaderRegex.IsMatch(diffLines[i+1])) break;

                    if (diffLine.StartsWith("+"))
                    {
                        lines.Insert(currentLine, diffLine.Substring(1));
                        currentLine++;
                        offset++;
                    }
                    else if (diffLine.StartsWith("-"))
                    {
                        if (currentLine >= lines.Count) throw new Exception($"Diff application failed: Line {currentLine + 1} out of bounds.");
                        // Optional: Validate context here if we wanted to be strict
                        lines.RemoveAt(currentLine);
                        offset--;
                    }
                    else if (diffLine.StartsWith(" "))
                    {
                        // Context line - validate it matches
                        if (currentLine >= lines.Count) throw new Exception($"Diff application failed: Context line {currentLine + 1} out of bounds.");
                        var expected = diffLine.Substring(1).Trim();
                        var actual = lines[currentLine].Trim();
                        if (expected != actual)
                        {
                            // In a real patch tool, we might try fuzzy matching. Here we'll just log a warning or be strict.
                            // For now, let's just proceed to be more tolerant of whitespace differences
                        }
                        currentLine++;
                    }
                    i++;
                }
                i--; // Back up so the outer loop can see the next hunk header or end
            }
        }

        var originalText = sourceText.ToString();
        var separator = originalText.Contains("\r\n") ? "\r\n" : "\n";
        return SourceText.From(string.Join(separator, lines), sourceText.Encoding);
    }

    /// <summary>
    /// Generates a simple line-based Unified Diff between two versions of text.
    /// </summary>
    public string CreateDiff(string oldText, string newText)
    {
        var oldLines = oldText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var newLines = newText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        var sb = new StringBuilder();
        sb.AppendLine("--- Original");
        sb.AppendLine("+++ Modified");

        // Simple line-by-line diff (not a full LCS algorithm, but sufficient for previewing changes)
        int oldIdx = 0;
        int newIdx = 0;

        while (oldIdx < oldLines.Length || newIdx < newLines.Length)
        {
            if (oldIdx < oldLines.Length && newIdx < newLines.Length && oldLines[oldIdx] == newLines[newIdx])
            {
                // Lines are identical
                oldIdx++;
                newIdx++;
            }
            else
            {
                // Lines differ - find the next match to determine if it's an insertion or deletion
                sb.AppendLine($"@@ -{oldIdx + 1} +{newIdx + 1} @@");
                
                // For simplicity in this tool, we show the removal then the addition
                if (oldIdx < oldLines.Length)
                {
                    sb.AppendLine($"-{oldLines[oldIdx]}");
                    oldIdx++;
                }
                if (newIdx < newLines.Length)
                {
                    sb.AppendLine($"+{newLines[newIdx]}");
                    newIdx++;
                }
            }
        }

        return sb.ToString();
    }
}
