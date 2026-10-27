using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for the write chokepoint (plan_eol_policy_and_write_chokepoint.md Step 6): fails when a production .cs file calls
// System.IO File.WriteAll*/AppendAll*/Create/CreateText/Copy outside RoslynSentinel.Common/FileIoHelper.cs.
// Workspace-file writes go through PersistentWorkspaceManager.ApplyProposedChangesAsync or EolFileWriter; other writes are allowlisted below.
// To add an entry, key it "<relative file>::<enclosing member>" with the exact call count and a reason; prefer routing the write instead.
// Only invocation nodes are counted, so comments and string literals never match. Counts may only shrink.
[TestFixture]
[Category("BareFileWriteRatchet")]
public class BareFileWriteRatchetTests
{
    // Allowlist keyed "<relative file>::<enclosing member>" to the exact number of bare File writes that member may contain.
    // A second write inside an allowlisted member fails. Each entry carries a reason. Counts only shrink.
    private static readonly Dictionary<string, int> BareFileWriteAllowlist = new(StringComparer.Ordinal)
    {
        // Agent-loop log and transcript files (non-workspace).
        ["RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs::FormatForAgentLog"] = 1,
        ["RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs::WriteTranscriptAsync"] = 1,
        // Large-result cache under .roslynsentinel/largeresults (server state, not workspace files).
        ["RoslynSentinel.Common/LargeResultHelper.cs::StoreLargeResultAsync"] = 1,
        ["RoslynSentinel.Common/LargeResultHelper.cs::StoreRawJsonAsync"] = 1,
        // Migration ledger state file (server state).
        ["RoslynSentinel.Common/MigrationLedger.cs::SaveAsync"] = 1,
        // Undo and operation blobs (server state, not workspace files).
        ["RoslynSentinel.Common/OperationBlobWriter.cs::WriteAsync"] = 1,
        ["RoslynSentinel.Common/OperationBlobWriter.cs::WriteValidationFailureAsync"] = 1,
        // .gitignore inside a throwaway build output directory.
        ["RoslynSentinel.Engines.Basic/BuildIsolation.cs::GetArtifactsPath"] = 1,
        // File.Copy of server binaries into a test instance directory (non-workspace).
        ["RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs::CopyServerOutput"] = 1,
        // .gitignore inside the test instance directory.
        ["RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs::CreateInstanceWorkingDirectory"] = 1,
        // Tool-list JSON dumps (WriteStartupDump writes two).
        ["RoslynSentinel.Server.Advanced/ConsoleMode.cs::ListTools"] = 1,
        ["RoslynSentinel.Server.Advanced/ConsoleMode.cs::WriteStartupDump"] = 2,
        // Crash log written at process start-up.
        ["RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs::AttachCrashHandlers"] = 1,
        // Debug dumps (two sites).
        ["RoslynSentinel.Tools.Advanced/AsyncifyTools.cs::AsyncifyLoopTool"] = 2,
        // Temp patch file for hunk staging, deleted after use.
        ["RoslynSentinel.Tools.Basic/GitImpl.cs::StageHunksAsync"] = 1,
        // Blocker .md document written by the PlanStepRunner utility.
        ["RoslynSentinel.Utilities.PlanStepRunner/Program.cs::WriteBlockerDoc"] = 1,
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

    private static bool IsFileReceiver(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "File",
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "File" } qualified => qualified.Expression.ToString() is "System.IO" or "IO",
        _ => false,
    };

    private static bool IsBareFileWrite(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Expression: var receiver } access || !IsFileReceiver(receiver))
        {
            return false;
        }

        var name = access.Name.Identifier.ValueText;
        if (name.EndsWith("Async", StringComparison.Ordinal))
        {
            name = name[..^"Async".Length];
        }

        return name.StartsWith("WriteAll", StringComparison.Ordinal)
            || name.StartsWith("AppendAll", StringComparison.Ordinal)
            || name is "Create" or "CreateText" or "Copy";
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
    && !directoryName.Contains("Tests", StringComparison.Ordinal)
    && !directoryName.Contains("Samples", StringComparison.Ordinal)
    && !directoryName.Contains("DummyConsole", StringComparison.Ordinal)
    && !directoryName.Contains("TempCheck", StringComparison.Ordinal);

    // Parses every production .cs file in every RoslynSentinel.* project (see IsProductionProject), skipping bin, obj and
    // Worktree folders. Returned in relative-path order for stable messages.
    private static List<(string RelativePath, SyntaxNode Root)> ParseProductionSources()
    {
        var repo = RepoRoot();
        var result = new List<(string RelativePath, SyntaxNode Root)>();
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

                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
                result.Add((relativePath, root));
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return result;
    }

    [Test]
    public void BareFileWrites_AreOnlyInTheAllowlist()
    {
        var sources = ParseProductionSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no production .cs files; the repo root lookup is broken.");

        // Bare write count per "<file>::<member>" key. The chokepoint helper itself is the only file exempt from the scan.
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (relativePath, root) in sources)
        {
            if (relativePath == "RoslynSentinel.Common/FileIoHelper.cs")
            {
                continue;
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (IsBareFileWrite(invocation))
                {
                    var key = relativePath + "::" + EnclosingMemberName(invocation);
                    actual[key] = actual.GetValueOrDefault(key) + 1;
                }
            }
        }

        var unexpected = actual.Keys.Where(key => !BareFileWriteAllowlist.ContainsKey(key)).ToList();
        var stale = BareFileWriteAllowlist.Keys.Where(key => !actual.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        var countMismatch = BareFileWriteAllowlist
            .Where(entry => actual.TryGetValue(entry.Key, out var count) && count != entry.Value)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"  {entry.Key}: allowlist says {entry.Value}, tree has {actual[entry.Key]}")
            .ToList();

        var problems = new List<string>();
        if (unexpected.Count > 0)
        {
            problems.Add("Unexpected bare System.IO File write call sites. Route each one through ApplyProposedChangesAsync (workspace files) "
                + "or EolFileWriter, or add a non-workspace allowlist entry with a reason:"
                + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Select(key => "  " + key + " (" + actual[key] + " call(s))")));
        }

        if (countMismatch.Count > 0)
        {
            problems.Add("Bare write count differs from the allowlist for an allowlisted member. A higher count is a new bare write to route "
                + "through the chokepoint; a lower count means the allowlist value must shrink to match:"
                + Environment.NewLine + string.Join(Environment.NewLine, countMismatch));
        }

        if (stale.Count > 0)
        {
            problems.Add("Stale allowlist entries (no longer present in the tree). Remove them so the list only shrinks:"
                + Environment.NewLine + string.Join(Environment.NewLine, stale.Select(key => "  " + key)));
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine + Environment.NewLine, problems));
    }
}
