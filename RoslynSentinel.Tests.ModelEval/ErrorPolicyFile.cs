using System.IO;

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
}
