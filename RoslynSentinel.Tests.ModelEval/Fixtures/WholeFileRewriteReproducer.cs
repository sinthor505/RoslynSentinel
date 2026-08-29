namespace RoslynSentinel.Tests.ModelEval.Fixtures;

/// <summary>
/// Minimal reproduction of a "whole-blob rewrite instead of scoped edit" bug pattern — the class of
/// bug plan-9b-model-test-step2.md was written against (a method rebuilds one node and then
/// reformats/rewrites the ENTIRE file instead of just the changed part). Uses plain string
/// manipulation rather than real Roslyn SyntaxNode APIs so it compiles inside
/// <see cref="RoslynSentinel.Tests.TestSolutionFixture"/>'s copy of Samples/ContosoOrders, which has
/// no NuGet packages and no restore step — the specific API surface isn't what this test exercises;
/// the agent loop, tool dispatch, and transcript/assertion machinery are.
/// </summary>
public static class WholeFileRewriteReproducer
{
    /// <summary>
    /// Goes in the fixture at ContosoOrders.Core/FixtureHelpers/BlockEditHelpers.cs — the "already
    /// has the fix pattern" file, standing in for a file like RefactoringEngine.cs where the
    /// scoped-edit helper already exists and is used elsewhere.
    /// </summary>
    public const string HelperFileContent = """
        namespace ContosoOrders.Core.FixtureHelpers;

        public static class BlockEditHelpers
        {
            /// <summary>
            /// Replaces <paramref name="oldBlock"/> with <paramref name="newBlock"/> inside
            /// <paramref name="fileText"/>, re-indenting only the replacement block to match the
            /// surrounding indentation — everything else in fileText is returned byte-for-byte
            /// unchanged.
            /// </summary>
            public static string ReplaceBlockFormatted(string fileText, string oldBlock, string newBlock)
            {
                var index = fileText.IndexOf(oldBlock, StringComparison.Ordinal);
                if (index < 0)
                {
                    throw new InvalidOperationException("oldBlock not found in fileText.");
                }

                var lineStart = fileText.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
                var indent = fileText[lineStart..index];

                var formattedNewBlock = string.Join(
                    "\n" + indent,
                    newBlock.Split('\n').Select(line => line.TrimEnd()));

                return fileText[..index] + formattedNewBlock + fileText[(index + oldBlock.Length)..];
            }
        }
        """;

    /// <summary>
    /// Goes in the fixture at ContosoOrders.Core/FixtureHelpers/BlockConverter.cs — the buggy file,
    /// standing in for a large multi-method engine file with one method that still has the bug.
    /// Deliberately padded with unrelated members before and after the buggy method, each with
    /// idiosyncratic spacing, so a whole-file reformat is mechanically detectable: those unrelated
    /// lines would change if a "reformat everything" call ran over the whole file, and would NOT
    /// change if the fix (rewrite only the touched region) is applied correctly.
    /// </summary>
    public const string BuggyFileContent = """
        namespace ContosoOrders.Core.FixtureHelpers;

        public class BlockConverter
        {
            private readonly object _unrelatedField = new();

            public string UnrelatedMethodBefore( int    x , int y )
            {
                    return (x+y).ToString();
            }

            /// <summary>
            /// Converts a "public abstract class Name { ... }" block into a "public interface IName
            /// { ... }" block by rewriting its header and stripping method bodies down to
            /// semicolons. BUG: rebuilds the whole file's text via ReformatWholeFile(), which
            /// re-indents every line in the file, not just the converted block.
            /// </summary>
            public string ConvertAbstractClassToInterface(string fileText, string className)
            {
                var oldHeader = $"public abstract class {className}";
                if (!fileText.Contains(oldHeader, StringComparison.Ordinal))
                {
                    return fileText;
                }

                var newHeader = $"public interface I{className}";
                var rewritten = fileText.Replace(oldHeader, newHeader, StringComparison.Ordinal);
                return ReformatWholeFile(rewritten);
            }

            private static string ReformatWholeFile(string fileText)
            {
                var lines = fileText.Split('\n');
                var normalized = lines.Select(line => line.TrimEnd());
                return string.Join("\n", normalized);
            }

            public string UnrelatedMethodAfter(  string   s  )
            {
                    return s?.Trim() ?? "";
            }
        }
        """;

    /// <summary>
    /// The abstract-class-shaped block the model's fixed method should be exercised against to
    /// confirm behavior is preserved (header still converts correctly), not just that the rewrite
    /// mechanism changed.
    /// </summary>
    public const string TargetAbstractClassFileContent = """
        namespace ContosoOrders.Core.FixtureHelpers;

        public abstract class Shape
        {
            public abstract double GetArea();

            public abstract double GetPerimeter();
        }
        """;
}
