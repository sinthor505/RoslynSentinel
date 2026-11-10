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
    /// The zero-match addendum: null unless the suite is fast, the caller gave a filter and no test matched. It says the
    /// fast suite excludes the OutOfProcess fixtures (which may hold the tests the filter meant) and names the next call.
    /// </summary>
    public static string? ZeroMatchHint(TestSuite suite, string? filter, int totalCount)
    {
        if (suite != TestSuite.fast || string.IsNullOrWhiteSpace(filter) || totalCount != 0)
        {
            return null;
        }

        return $"The fast suite excludes [Category(\"{OutOfProcessCategory}\")] fixtures (git/build/test-run/process tests), which may hold the tests this filter names. "
            + ToolCallHint.Next("RunTest", "to include them.", ("suite", "full"));
    }
}
