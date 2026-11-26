namespace RoslynSentinel.Common;

/// <summary>
/// Startup settings for the inline budget: how many characters a tool result body may fill inline before it is
/// paged. Populated once at startup via <see cref="Configure"/>: <c>--inline-budget-chars</c> first, then the
/// <c>ROSLYNSENTINEL_INLINE_BUDGET_CHARS</c> environment variable. Echo and stamps are extra on top of the budget.
/// Read-only after startup.
/// </summary>
[StartupSettingGroup("Inline budget")]
public static class InlineBudgetOptions
{
    /// <summary>The stock budget: 15 KiB of result body characters.</summary>
    public const int StockBudgetChars = 15 * 1024;

    /// <summary>The smallest budget a setting may configure.</summary>
    public const int MinimumBudgetChars = 8192;

    /// <summary>The startup setting backing <see cref="BudgetChars"/>.</summary>
    public static readonly StartupSetting<int> BudgetCharsSetting = StartupSetting.Int("--inline-budget-chars", "ROSLYNSENTINEL_INLINE_BUDGET_CHARS", StockBudgetChars, "Chars a tool result body may fill inline before it is paged; echo and stamps are extra.", SettingScope.Server, minValue: MinimumBudgetChars);

    /// <summary>The configured inline budget in characters.</summary>
    public static int BudgetChars => BudgetCharsSetting.Value;

    /// <summary>Populates the settings from args and environment. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(InlineBudgetOptions), args);
    }
}
