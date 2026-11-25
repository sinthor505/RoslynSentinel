using System;
using System.Collections.Generic;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Splices the needed project edges into a project file's bytes.</summary>
public static class CsprojEdgeWriter
{
    /// <summary>Returns the project file text with the given edges spliced in as ProjectReference or InternalsVisibleTo items.</summary>
    /// <remarks>The bytes are decoded as UTF-8 and a leading BOM stays as U+FEFF, so writing the returned text back verbatim reproduces the bytes. Edges already present are skipped, so splicing the output again changes nothing.</remarks>
    /// <param name="projectReferenceInclude">Maps a referenced project name to its ProjectReference Include path; null assumes sibling project folders.</param>
    /// <param name="internalsVisibleToName">Maps a receiving project name to its InternalsVisibleTo Include value (the friend assembly name); null uses the project name.</param>
    public static string Splice(byte[] csprojBytes, IReadOnlyList<NeededProjectEdge> edgesForThisProject, Func<string, string>? projectReferenceInclude = null, Func<string, string>? internalsVisibleToName = null)
    {
        var text = System.Text.Encoding.UTF8.GetString(csprojBytes);
        var eol = CopyAndObsoleteEdits.DetectEol(text);
        var unit = DetectUnit(text);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<string>();
        foreach (var kind in new[] { ProjectEdgeKind.ProjectReference, ProjectEdgeKind.InternalsVisibleTo })
        {
            foreach (var edge in edgesForThisProject)
            {
                if (edge.Kind != kind)
                {
                    continue;
                }

                var tag = kind == ProjectEdgeKind.ProjectReference ? "ProjectReference" : "InternalsVisibleTo";
                // MoveFileTool passes the real relative csproj path of the referenced project; the null default (sibling project folders) is only for a caller that has no project file paths.
                var value = kind == ProjectEdgeKind.ProjectReference
                    ? (projectReferenceInclude?.Invoke(edge.ToProject) ?? $@"..\{edge.ToProject}\{edge.ToProject}.csproj")
                    : (internalsVisibleToName?.Invoke(edge.ToProject) ?? edge.ToProject); // an InternalsVisibleTo Include names the receiving ASSEMBLY, which can differ from the project name
                var escaped = EscapeAttribute(value);
                if (text.Contains($"<{tag} Include=\"{escaped}\"", StringComparison.OrdinalIgnoreCase)
                    || !seen.Add($"{tag}|{escaped}"))
                {
                    continue;
                }

                items.Add($"{unit}{unit}<{tag} Include=\"{escaped}\" />");
            }
        }

        if (items.Count == 0)
        {
            return text;
        }

        var closeIndex = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            throw new ArgumentException("The project file has no closing </Project> tag, so the edges cannot be added.");
        }

        var lineStart = closeIndex == 0 ? 0 : text.LastIndexOf('\n', closeIndex - 1) + 1;
        var insertAt = lineStart;
        var block = new System.Text.StringBuilder();
        if (text.Substring(lineStart, closeIndex - lineStart).Trim().Length > 0)
        {
            // The closing tag shares its line with other markup, so the block starts on a new line before it.
            insertAt = closeIndex;
            block.Append(eol);
        }

        block.Append(unit).Append("<ItemGroup>").Append(eol);
        foreach (var item in items)
        {
            block.Append(item).Append(eol);
        }

        block.Append(unit).Append("</ItemGroup>").Append(eol);
        return text.Insert(insertAt, block.ToString());
    }

    private static string DetectUnit(string text)
    {
        var tabLines = 0;
        var spaceLines = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                tabLines++;
            }
            else if (line.StartsWith(' '))
            {
                spaceLines++;
            }
        }

        return tabLines > 0 && tabLines > spaceLines ? "\t" : "  ";
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
