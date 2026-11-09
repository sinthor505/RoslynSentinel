using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for the stale-read policy: every production call to ApplyProposedChangesAsync must pass a named staleReadPolicy: argument,
// so the write chokepoint runs the observed-content check on purpose rather than by default.
// Exemptions are keyed "<relative file>::<enclosing member>" with a one-line reason; a key exempts every call in that member.
// Only invocation nodes are counted, so comments and string literals never match. The allowlist only shrinks (see the second test).
// Excluded: RoslynSentinel.Common (the chokepoint's own implementations and wrappers), test projects, and bin, obj and Worktree folders.
[TestFixture]
[Category("StaleReadPolicyRatchet")]
public class StaleReadPolicyRatchetTests
{
    private const string ApplyMethodName = "ApplyProposedChangesAsync";
    private const string PolicyArgumentName = "staleReadPolicy";

    // Allowlist keyed "<relative file>::<enclosing member>" to a one-line reason.
    private static readonly Dictionary<string, string> StaleReadPolicyAllowlist = new(StringComparer.Ordinal)
    {
        ["RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs::UndoLastApply"] = "exactRestore: undo restores a recorded pre-image and is exempt from the stale-read check",
        ["RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs::CreateFile"] = "a new file has nothing observed, exempt by decision",
    };

    [Test]
    public void ApplyProposedChangesCalls_PassAnExplicitStaleReadPolicyOrAreAllowlisted()
    {
        var sources = ParseProductionSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no production .cs files; the repo root lookup is broken.");

        var violations = new List<string>();
        foreach (var (relativePath, text) in sources)
        {
            foreach (var (member, line) in FindUnguardedApplyCalls(text))
            {
                if (!StaleReadPolicyAllowlist.ContainsKey(relativePath + "::" + member))
                {
                    violations.Add("  VIOLATION: " + relativePath + ":" + line + " (" + member + ")");
                }
            }
        }

        Assert.That(violations, Is.Empty,
            "ApplyProposedChangesAsync call sites without a named staleReadPolicy: argument. Pass staleReadPolicy: explicitly, "
            + "or add a one-line allowlist reason keyed \"<file>::<member>\" if the call is exempt by decision:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void StaleReadPolicyAllowlist_OnlyShrinks_EveryKeyStillMatchesAnUnguardedCall()
    {
        var sources = ParseProductionSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no production .cs files; the repo root lookup is broken.");

        var unguardedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, text) in sources)
        {
            foreach (var (member, _) in FindUnguardedApplyCalls(text))
            {
                unguardedKeys.Add(relativePath + "::" + member);
            }
        }

        var stale = StaleReadPolicyAllowlist.Keys.Where(key => !unguardedKeys.Contains(key)).Order(StringComparer.Ordinal).ToList();
        Assert.That(stale, Is.Empty,
            "Stale allowlist entries: no ApplyProposedChangesAsync call without staleReadPolicy: remains under these keys. "
            + "Remove them so the list only shrinks:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale.Select(key => "  " + key)));
    }

    [Test]
    public void Scanner_FlagsUnguardedCalls_IgnoresGuardedAndCommentedCalls()
    {
        const string snippet = """
            using System.Threading.Tasks;

            namespace Snippet;

            public class Writer
            {
                private readonly IWorkspaceMutator _manager = null!;

                public async Task Reverted(Dictionary<FilePathWrapper, string> changes)
                {
                    await _manager.ApplyProposedChangesAsync(changes);
                }

                public async Task BareReverted(Dictionary<FilePathWrapper, string> changes)
                {
                    await ApplyProposedChangesAsync(changes);
                }

                public async Task Guarded(Dictionary<FilePathWrapper, string> changes)
                {
                    await _manager.ApplyProposedChangesAsync(changes, staleReadPolicy: StaleReadPolicy.Refuse);
                }

                public void Commented()
                {
                    // await _manager.ApplyProposedChangesAsync(changes);
                    /* await ApplyProposedChangesAsync(changes); */
                    var text = "_manager.ApplyProposedChangesAsync(changes)";
                }
            }
            """;

        var found = FindUnguardedApplyCalls(snippet);

        Assert.That(found.Select(call => call.Member).ToList(), Is.EqualTo(new[] { "Reverted", "BareReverted" }),
            "Scanner must report the member-access and bare-identifier calls without staleReadPolicy, and nothing else.");
        Assert.That(found.All(call => call.Line > 0), Is.True, "Every reported call must carry a 1-based line number.");
    }

    // Returns (enclosing member, 1-based line) for each ApplyProposedChangesAsync invocation in the source text that lacks a
    // named staleReadPolicy: argument. Covers member access, conditional access and bare identifier calls. Invocation nodes only,
    // so comments and string literals never match.
    internal static List<(string Member, int Line)> FindUnguardedApplyCalls(string sourceText)
    {
        var root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();
        var result = new List<(string Member, int Line)>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (InvokedMemberName(invocation) != ApplyMethodName || HasStaleReadPolicyArgument(invocation))
            {
                continue;
            }

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            result.Add((EnclosingMemberName(invocation), line));
        }

        return result;
    }

    private static bool HasStaleReadPolicyArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.Any(argument => argument.NameColon?.Name.Identifier.ValueText == PolicyArgumentName);

    private static string? InvokedMemberName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    private static string EnclosingMemberName(SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            null => "<no member>",
            _ => "<" + member.Kind() + ">",
        };
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool IsProductionProject(string directoryName) =>
        directoryName.StartsWith("RoslynSentinel.", StringComparison.Ordinal)
        && directoryName != "RoslynSentinel.Common"
        && !directoryName.Contains("Tests", StringComparison.Ordinal)
        && !directoryName.Contains("Samples", StringComparison.Ordinal)
        && !directoryName.Contains("DummyConsole", StringComparison.Ordinal)
        && !directoryName.Contains("TempCheck", StringComparison.Ordinal);

    // Reads every production .cs file in every RoslynSentinel.* project (see IsProductionProject), skipping bin, obj and
    // Worktree folders. Returned in relative-path order for stable messages.
    private static List<(string RelativePath, string Text)> ParseProductionSources()
    {
        var repo = RepoRoot();
        var result = new List<(string RelativePath, string Text)>();
        foreach (var projectDir in Directory.GetDirectories(repo))
        {
            if (!IsProductionProject(Path.GetFileName(projectDir)))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (relativePath.Split('/').Any(segment => segment is "bin" or "obj" or "Worktree"))
                {
                    continue;
                }

                result.Add((relativePath, File.ReadAllText(file)));
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return result;
    }
}
