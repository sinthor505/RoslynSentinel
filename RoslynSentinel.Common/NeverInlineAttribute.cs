namespace RoslynSentinel.Common;

/// <summary>
/// Marks a property of a tool's success-data type whose content must never be returned inline: bulk that only
/// belongs in the large-result store (for example the full diagnostics list or changed file contents).
/// <see cref="ResultPager"/> always moves it out and reports it in the omitted list, whatever the result size.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NeverInlineAttribute : Attribute
{
}
