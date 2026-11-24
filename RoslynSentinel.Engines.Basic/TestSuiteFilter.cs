namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Composes the `dotnet test --filter` expression for a RunTest <see cref="TestSuite"/>. The fast suite excludes the
/// fixtures that carry <c>[Category("OutOfProcess")]</c> (they start git, dotnet or powershell processes; measured
/// 2026-11-22 with scripts/Get-TestRunTimings.ps1 over 47 full runs: a median 79% of Tests.Tools.Basic test-seconds and
/// about 520 of its 628 serial seconds); the full suite passes the caller's filter through unchanged. The expression uses the NUnit
/// adapter's <c>TestCategory</c> property; the engine applies it only to projects that reference the NUnit adapter
/// (see <see cref="TestProjectDetector.UsesNUnitAdapter"/>), because another adapter rejects an unknown property.
/// </summary>
public static class TestSuiteFilter
{
    /// <summary>The category the fast suite excludes.</summary>
    public const string OutOfProcessCategory = "OutOfProcess";

    /// <summary>The filter term that excludes <see cref="OutOfProcessCategory"/> fixtures.</summary>
    public const string ExcludeOutOfProcess = "TestCategory!=" + OutOfProcessCategory;

    /// <summary>
    /// full: the filter unchanged (possibly null). fast: <see cref="ExcludeOutOfProcess"/> when no filter is given,
    /// otherwise <c>(filter)&amp;TestCategory!=OutOfProcess</c> so an alternation in the caller's filter keeps its meaning.
    /// </summary>
    public static string? Compose(string? filter, TestSuite suite)
    {
        if (suite == TestSuite.full)
        {
            return filter;
        }

        return string.IsNullOrWhiteSpace(filter)
            ? ExcludeOutOfProcess
            : $"({filter.Trim()})&{ExcludeOutOfProcess}";
    }

    /// <summary>
    /// The zero-match addendum: null unless the caller gave a filter and no test matched, on any suite. It says no test
    /// matched, so nothing ran and nothing was verified, and names the next call: check the test name with a Search, or
    /// drop the filter. On the fast suite only it adds a secondary clause: the fast suite also skips the OutOfProcess
    /// fixtures, which may hold the test, and it names the full run with the caller's filter, and scope and scopeName
    /// when the scope is not solution, so the call can be pasted as written. <paramref name="scope"/> and
    /// <paramref name="scopeName"/> are optional trailing parameters; omitted, the full-run call names the solution scope.
    /// </summary>
    public static string? ZeroMatchHint(TestSuite suite, string? filter, int totalCount, ToolScope scope = ToolScope.solution, string? scopeName = null)
    {
        if (string.IsNullOrWhiteSpace(filter) || totalCount != 0)
        {
            return null;
        }

        var hint = "No test matched the filter, so nothing ran and nothing was verified. Confirm the test name exists with "
            + ToolCallHint.Call("Search", ("mode", "method"), ("query", "<TestMethodName>"))
            + ", or drop the filter.";

        if (suite != TestSuite.fast)
        {
            return hint;
        }

        return hint
            + $" The fast suite also skips [Category(\"{OutOfProcessCategory}\")] fixtures; if the test lives in one, "
            + ToolCallHint.Next("RunTest", "to include them.", FullRunArguments(filter, scope, scopeName));
    }

    /// <summary>
    /// The full-run call arguments shared by the zero-match and all-skipped notes: the full suite with the caller's filter,
    /// plus scope and scopeName when the scope is not solution.
    /// </summary>
    private static (string Name, string Value)[] FullRunArguments(string filter, ToolScope scope, string? scopeName)
    {
        var arguments = new List<(string Name, string Value)> { ("suite", "full"), ("filter", filter) };
        if (scope != ToolScope.solution)
        {
            arguments.Add(("scope", scope.ToString()));
            if (!string.IsNullOrWhiteSpace(scopeName))
            {
                arguments.Add(("scopeName", scopeName));
            }
        }

        return arguments.ToArray();
    }

    /// <summary>
    /// The all-skipped addendum: null unless the fast suite ran a filter that matched tests, none passed or failed, and at
    /// least one was skipped. It states the facts: the fast suite adds the OutOfProcess exclusion to the filter, which can
    /// keep the test adapter from treating the filter as an explicit selection of [Explicit] fixtures. It names the full run
    /// with the caller's filter, scope and scopeName, so the call can be pasted as written.
    /// </summary>
    public static string? AllSkippedNote(TestSuite suite, string? filter, int passedCount, int failedCount, int skippedCount, ToolScope scope = ToolScope.solution, string? scopeName = null)
    {
        if (suite != TestSuite.fast || string.IsNullOrWhiteSpace(filter) || passedCount != 0 || failedCount != 0 || skippedCount == 0)
        {
            return null;
        }

        return $"Every test the filter matched was skipped ({skippedCount} skipped, 0 passed). The fast suite adds {ExcludeOutOfProcess} to the filter, which can keep the test adapter from treating the filter as an explicit selection of [Explicit] fixtures; "
            + ToolCallHint.Next("RunTest", "to run the filter as written.", FullRunArguments(filter, scope, scopeName));
    }
}
