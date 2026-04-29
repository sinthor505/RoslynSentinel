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
    /// </summary>
    public SourceText ApplyDiff(SourceText sourceText, string unifiedDiff)
    {
        var lines = sourceText.Lines.Select(l => l.ToString()).ToList();
        var diffLines = unifiedDiff.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        int currentLine = 0;
        var hunkHeaderRegex = new Regex(@"^@@\s+\-(\d+),?\d*\s+\+(\d+),?\d*\s+@@");

        for (int i = 0; i < diffLines.Length; i++)
        {
            var line = diffLines[i];
            var match = hunkHeaderRegex.Match(line);

            if (match.Success)
            {
                currentLine = int.Parse(match.Groups[2].Value) - 1;
                continue;
            }

            if (line.StartsWith("+") && !line.StartsWith("+++"))
            {
                lines.Insert(currentLine, line.Substring(1));
                currentLine++;
            }
            else if (line.StartsWith("-") && !line.StartsWith("---"))
            {
                if (currentLine < lines.Count) lines.RemoveAt(currentLine);
            }
            else if (line.StartsWith(" "))
            {
                currentLine++;
            }
        }

        return SourceText.From(string.Join(Environment.NewLine, lines));
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
