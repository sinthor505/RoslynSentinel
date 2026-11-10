namespace RoslynSentinel.Common;

/// <summary>Optional knobs for <see cref="GitProcess.Run"/> / <see cref="GitProcess.RunAsync"/>; null options means all defaults.</summary>
internal sealed record GitRunOptions
{
    /// <summary>Path of the git executable (see <see cref="GitProcess.ResolveGitExecutablePath"/>); null runs the bare name "git".</summary>
    internal string? GitExecutable { get; init; }

    /// <summary>Arguments placed before the caller's git arguments, normally <c>-c key=value</c> pairs.</summary>
    internal IReadOnlyList<string>? PrefixArgs { get; init; }

    /// <summary>True returns stdout byte-exact in <see cref="GitRunResult.StdOutBytes"/> instead of decoded text (a diff that is re-applied as a patch).</summary>
    internal bool StdOutAsBytes { get; init; }
}
