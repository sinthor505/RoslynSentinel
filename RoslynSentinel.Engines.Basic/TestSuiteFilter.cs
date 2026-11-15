namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Composes the `dotnet test --filter` expression for a RunTest <see cref="TestSuite"/>. The fast suite excludes the
/// fixtures that carry <c>[Category("OutOfProcess")]</c> (they start git, dotnet or powershell processes and take about
/// 40% of the test time); the full suite passes the caller's filter through unchanged. The expression uses the NUnit
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

        var fullRunArguments = new List<(string Name, string Value)> { ("suite", "full"), ("filter", filter) };
        if (scope != ToolScope.solution)
        {
            fullRunArguments.Add(("scope", scope.ToString()));
            if (!string.IsNullOrWhiteSpace(scopeName))
            {
                fullRunArguments.Add(("scopeName", scopeName));
            }
        }

        return hint
            + $" The fast suite also skips [Category(\"{OutOfProcessCategory}\")] fixtures; if the test lives in one, "
            + ToolCallHint.Next("RunTest", "to include them.", fullRunArguments.ToArray());
    }
}
