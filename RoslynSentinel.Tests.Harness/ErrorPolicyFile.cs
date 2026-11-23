using System.IO;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// The error-policy Markdown file used by the error-policy A/B. The project copies
/// <c>error-policy.md</c> to the test output directory; the eval script or harness sets
/// ROSLYNSENTINEL_ERROR_POLICY_FILE to <see cref="OutputPath"/>.
/// </summary>
public static class ErrorPolicyFile
{
    /// <summary>File name of the policy document, both in the project folder and in the output directory.</summary>
    public const string FileName = "error-policy.md";

    /// <summary>Absolute path of the copied policy file in the test output directory.</summary>
    public static string OutputPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Builds the <c>ErrorPolicyOptions.Configure</c> arguments for the ModelEval harness. For a Pointer or Inline mode
    /// with no policy file given, the file defaults to <see cref="OutputPath"/>. Off, or any mode with a file given,
    /// returns no arguments. An unknown mode throws <see cref="ArgumentException"/>, as <see cref="ErrorPolicyOptions.ParseMode"/> does.
    /// </summary>
    /// <remarks>
    /// Deviation from production startup, which fails fast for a mode without a file. This is a ModelEval-harness
    /// convenience only; server startup keeps its behaviour.
    /// </remarks>
    public static string[] ResolveConfigureArgs(string? modeRaw, string? fileRaw)
    {
        // Deliberate deviation from production startup (fails fast for a mode without a file); harness convenience only.
        if (ErrorPolicyOptions.ParseMode(modeRaw) == ErrorPolicyMode.Off || !string.IsNullOrWhiteSpace(fileRaw))
        {
            return [];
        }

        return ["--error-policy-file=" + OutputPath];
    }
}
