using System.Text;

namespace RoslynSentinel.Tests.ModelEval.Fixtures;

/// <summary>
/// Size-parameterized variant of <see cref="WholeFileRewriteReproducer"/>, built to bisect the file
/// size / diff payload size at which a local model's <c>ApplyDiff</c> success rate drops off. Same
/// bug shape and same required fix (copy a private helper into the buggy file, rewire one method
/// call, leave everything else byte-for-byte alone) at every size — the only thing that changes is
/// <paramref name="unrelatedMethodCount"/> padding methods before the buggy method, each numbered
/// and distinctly bodied so a whole-file reformat (the bug) or an accidental drop/reorder (a bad
/// diff apply) is mechanically detectable per-method, not just via a handful of anchor lines.
/// </summary>
public static class SizeGraduatedReproducer
{
    /// <summary>Same private helper as <see cref="WholeFileRewriteReproducer.HelperFileContent"/> — content is size-independent, so it is not parameterized.</summary>
    public const string HelperFileContent = WholeFileRewriteReproducer.HelperFileContent;

    /// <summary>
    /// Builds a BlockConverter.cs-shaped file with <paramref name="unrelatedMethodCount"/> padding
    /// methods (each with idiosyncratic, easy-to-diff-against spacing) inserted before the buggy
    /// <c>ConvertAbstractClassToInterface</c> method. Method N returns <c>$"unrelated-{N}"</c> so a
    /// dropped/duplicated/reordered method is trivially detectable by scanning for the expected
    /// sequence, not just string-containment.
    /// </summary>
    public static string BuildBuggyFileContent(int unrelatedMethodCount)
    {
        var sb = new StringBuilder();
        sb.Append("namespace ContosoOrders.Core.FixtureHelpers;\n\n");
        sb.Append("public class BlockConverter\n{\n");
        sb.Append("    private readonly object _unrelatedField = new();\n\n");

        for (var i = 0; i < unrelatedMethodCount; i++)
        {
            sb.Append(BuildPaddingMethod(i));
            sb.Append('\n');
        }

        sb.Append("""
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


            """);

        sb.Append("    public string UnrelatedMethodAfter(  string   s  )\n");
        sb.Append("    {\n");
        sb.Append("            return s?.Trim() ?? \"\";\n");
        sb.Append("    }\n");
        sb.Append("}\n");

        return sb.ToString();
    }

    /// <summary>
    /// One padding method, numbered and oddly-spaced (spacing varies deterministically by index so
    /// no two padding methods are byte-identical, preventing an "accidentally reordered but still
    /// looks right" false pass).
    /// </summary>
    private static string BuildPaddingMethod(int index)
    {
        var extraSpace = new string(' ', 1 + (index % 3));
        return $$"""
                public string UnrelatedMethod{{index}}(  int{{extraSpace}}value  )
                {
                        return $"unrelated-{{index}}-{value}";
                }

            """;
    }

    /// <summary>Same target file as <see cref="WholeFileRewriteReproducer.TargetAbstractClassFileContent"/> — size-independent.</summary>
    public const string TargetAbstractClassFileContent = WholeFileRewriteReproducer.TargetAbstractClassFileContent;
}
