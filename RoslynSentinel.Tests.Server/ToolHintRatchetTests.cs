using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.Server;

// Ratchet for hand-written tool-call hints. Production strings that name an MCP tool with call
// syntax must be built with ToolCallHint.Call/Next, or carry a `// hint-ok: reason` comment.
// The baseline file may only shrink.
[TestFixture]
public class ToolHintRatchetTests
{
    private const string BaselineRelativePath = "docs/known-handwritten-tool-hints.txt";
    private const string UpdateEnvVar = "ROSLYNSENTINEL_UPDATE_HINT_BASELINE";
    private const string HintOkPrefix = "// hint-ok:";
    private const string SamplePath = "RoslynSentinel.Tests.Server/Sample.cs";

    private static readonly string[] Triggers =
    {
        "Call ", "Use ", "Next: ", "run ", "Run ", "retry ", "Retry ", "via ", "with ", "or ", "and ",
        "then ", "Then ", "call ", "use ",
    };

    private static readonly string[] SkippedFileNames =
    {
        "ToolCallHint.cs", "LargeResultHints.cs", "ToolSignatureCatalog.cs", "ToolSignatureCatalogReflection.cs",
    };

    [Test]
    public void Scan_MatchesBaseline()
    {
        // Force-load every tool assembly so the reflection catalog lists all tools.
        _ = Assembly.Load("RoslynSentinel.Tools.Basic");
        _ = Assembly.Load("RoslynSentinel.Tools.Advanced");
        _ = Assembly.Load("RoslynSentinel.Tools.Experimental");
        var toolNames = ToolSignatureCatalogReflection.FromLoadedAssemblies().ToolNames.ToList();
        Assert.That(toolNames, Does.Contain("LoadSolution").And.Contain("Search").And.Contain("GetFileOutline"),
            "tool catalog is incomplete: a tool assembly was not loaded");

        var repoRoot = FindRepoRoot();
        var baselinePath = Path.Combine(repoRoot, BaselineRelativePath);
        var found = ScanRepo(repoRoot, toolNames);

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            WriteBaseline(baselinePath, found);
            var perTool = found.GroupBy(pair => ToolOf(pair.Key))
                .Select(group => new { Tool = group.Key, Hits = group.Sum(pair => pair.Value) })
                .OrderByDescending(row => row.Hits)
                .ThenBy(row => row.Tool, StringComparer.Ordinal)
                .Take(8)
                .Select(row => $"{row.Tool}={row.Hits}");
            TestContext.Out.WriteLine($"baseline lines: {found.Count}; total hits: {found.Values.Sum()}; top tools: {string.Join(", ", perTool)}");
            return;
        }

        var baseline = LoadBaseline(baselinePath);
        var problems = new List<string>();
        foreach (var (key, count) in found.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var allowed = baseline.GetValueOrDefault(key);
            if (count > allowed)
            {
                problems.Add($"NEW hint: {key} | found {count}, baseline {allowed}. Build the hint with ToolCallHint.Call/Next (RoslynSentinel.Common) or add a `// hint-ok: reason` comment.");
            }
        }

        foreach (var (key, allowed) in baseline.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(key);
            if (count < allowed)
            {
                problems.Add($"STALE baseline: {key} | baseline {allowed}, found {count}. Lower or remove this line (the ratchet only tightens).");
            }
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void Scan_FindsKnownHintShapes()
    {
        var tools = new[] { "LoadSolution", "Search", "Member", "Build", "ReadFile" };

        var callPhrase = ScanText(SamplePath, """
            class C
            {
                string M() => "Call LoadSolution with a path";
            }
            """, tools);
        var callSyntax = ScanText(SamplePath, """
            class C
            {
                string M() => "Search(mode: symbol)";
            }
            """, tools);
        var prose = ScanText(SamplePath, """
            class C
            {
                string M() => "Search finds the matching symbols";
            }
            """, tools);
        var attribute = ScanText(SamplePath, """
            class C
            {
                [Description("Call LoadSolution")]
                public string P => "";
            }
            """, tools);
        var hintOk = ScanText(SamplePath, """
            class C
            {
                // hint-ok: legacy wording kept for a test fixture
                string M() => "Call LoadSolution with a path";
            }
            """, tools);
        var namesOnly = ScanText(SamplePath, """
            class C
            {
                string M() => "Member.Name; Build.cs";
            }
            """, tools);

        Assert.Multiple(() =>
        {
            Assert.That(CountFor(callPhrase, "LoadSolution"), Is.EqualTo(1), "Call + name is a hit");
            Assert.That(callPhrase.ContainsKey(SamplePath + " | M | LoadSolution"), Is.True, "key shape is path | member | tool");
            Assert.That(CountFor(callSyntax, "Search"), Is.EqualTo(1), "name followed by ( is a hit");
            Assert.That(CountFor(prose, "Search"), Is.EqualTo(0), "plain prose is not a hit");
            Assert.That(CountFor(attribute, "LoadSolution"), Is.EqualTo(0), "attribute arguments are out of scope");
            Assert.That(CountFor(hintOk, "LoadSolution"), Is.EqualTo(0), "hint-ok comment exempts the statement");
            Assert.That(CountFor(namesOnly, "Member"), Is.EqualTo(0), "Member.Name is not a call");
            Assert.That(CountFor(namesOnly, "Build"), Is.EqualTo(0), "Build.cs is not a call");
        });
    }

    internal static Dictionary<string, int> ScanText(string relativePath, string sourceText, IEnumerable<string> toolNames)
    {
        var names = toolNames.Distinct(StringComparer.Ordinal).ToList();
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        var root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();
        foreach (var token in root.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.InterpolatedStringTextToken))
            {
                continue;
            }

            if (token.Parent is null || token.Parent.AncestorsAndSelf().Any(node => node is AttributeSyntax))
            {
                continue;
            }

            var owner = token.Parent.AncestorsAndSelf().FirstOrDefault(node => node is StatementSyntax or MemberDeclarationSyntax);
            if (owner is not null && HasHintOk(owner))
            {
                continue;
            }

            var text = token.ValueText;
            if (text.Length == 0)
            {
                continue;
            }

            foreach (var name in names)
            {
                var count = CountHits(text, name);
                if (count == 0)
                {
                    continue;
                }

                var key = $"{relativePath} | {EnclosingMember(token)} | {name}";
                hits[key] = hits.GetValueOrDefault(key) + count;
            }
        }

        return hits;
    }

    private static bool HasHintOk(SyntaxNode node)
    {
        return node.GetLeadingTrivia().Concat(node.GetTrailingTrivia()).Any(trivia =>
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && trivia.ToString().TrimStart().StartsWith(HintOkPrefix, StringComparison.Ordinal));
    }

    private static int CountHits(string text, string name)
    {
        var count = 0;
        var index = text.IndexOf(name, 0, StringComparison.Ordinal);
        while (index >= 0)
        {
            var end = index + name.Length;
            var boundaryBefore = index == 0 || !IsBeforeBlocker(text[index - 1]);
            var boundaryAfter = end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            if (boundaryBefore && boundaryAfter && (IsCallParen(text, end) || PrecededByTrigger(text, index)))
            {
                count++;
            }

            index = text.IndexOf(name, end, StringComparison.Ordinal);
        }

        return count;
    }

    private static bool IsBeforeBlocker(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '"';
    }

    private static bool IsCallParen(string text, int end)
    {
        return end < text.Length && text[end] == '(';
    }

    private static bool PrecededByTrigger(string text, int index)
    {
        var prefix = text[..index];
        return Triggers.Any(trigger => prefix.EndsWith(trigger, StringComparison.Ordinal));
    }

    private static Dictionary<string, int> ScanRepo(string repoRoot, IEnumerable<string> toolNames)
    {
        var names = toolNames.ToList();
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var separator = Path.DirectorySeparatorChar;
        var projectDirs = Directory.EnumerateDirectories(repoRoot, "RoslynSentinel.*")
            .Where(dir => !Path.GetFileName(dir).Contains(".Tests", StringComparison.Ordinal)
                          && Path.GetFileName(dir) != "RoslynSentinel.Tests");
        foreach (var dir in projectDirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, file);
                var segments = relative.Split(separator);
                if (segments.Any(segment => segment is "bin" or "obj" or "Worktree")
                    || SkippedFileNames.Contains(segments[^1], StringComparer.Ordinal))
                {
                    continue;
                }

                var relativeKey = relative.Replace(separator, '/');
                foreach (var (key, count) in ScanText(relativeKey, File.ReadAllText(file), names))
                {
                    totals[key] = totals.GetValueOrDefault(key) + count;
                }
            }
        }

        return totals;
    }

    private static string EnclosingMember(SyntaxToken token)
    {
        if (token.Parent is null)
        {
            return "<top-level>";
        }

        foreach (var node in token.Parent.AncestorsAndSelf())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.ValueText;
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Identifier.ValueText;
                case DestructorDeclarationSyntax dtor:
                    return dtor.Identifier.ValueText;
                case LocalFunctionStatementSyntax local:
                    return local.Identifier.ValueText;
                case PropertyDeclarationSyntax property when property.Initializer is not null && property.Initializer.Span.Contains(token.Span):
                    return EnclosingTypeName(property);
                case PropertyDeclarationSyntax property:
                    return property.Identifier.ValueText;
                case FieldDeclarationSyntax field:
                    return EnclosingTypeName(field);
            }
        }

        return "<top-level>";
    }

    private static string EnclosingTypeName(SyntaxNode node)
    {
        return node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<global>";
    }

    private static string ToolOf(string key)
    {
        return key[(key.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..];
    }

    private static int CountFor(Dictionary<string, int> hits, string tool)
    {
        return hits.Where(pair => ToolOf(pair.Key) == tool).Sum(pair => pair.Value);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, int> LoadBaseline(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var split = line.LastIndexOf(" | ", StringComparison.Ordinal);
            if (split < 0 || !int.TryParse(line[(split + 3)..], out var count))
            {
                throw new InvalidDataException("Malformed hint baseline line: " + line);
            }

            result[line[..split]] = count;
        }

        return result;
    }

    private static void WriteBaseline(string path, Dictionary<string, int> hits)
    {
        var lines = new List<string>
        {
            "# Known hand-written tool-call hints: ratchet baseline for ToolHintRatchetTests.",
            "# New hints must be built with ToolCallHint.Call/Next (RoslynSentinel.Common), or carry a",
            "# `// hint-ok: <reason>` comment on their statement. This file may only shrink.",
            "# Refresh after removing hints: set ROSLYNSENTINEL_UPDATE_HINT_BASELINE=1 and run",
            "# ToolHintRatchetTests.Scan_MatchesBaseline. Line format: <path> | <member> | <tool> | <count>",
        };
        lines.AddRange(hits.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} | {pair.Value}"));
        File.WriteAllLines(path, lines);
    }
}
