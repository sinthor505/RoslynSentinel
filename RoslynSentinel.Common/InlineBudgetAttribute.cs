namespace RoslynSentinel.Common;

/// <summary>
/// Declares the inline budget class of a tool. Put it on an <c>[McpServerTool]</c> method; the result
/// marshaller reads it through the method's MethodInfo. Not honoured in marshaller mode <c>off</c>
/// (failures still get the Whole threshold through the body's isError).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InlineBudgetAttribute(InlineBudgetClass budgetClass) : Attribute
{
    /// <summary>The budget class the marshaller applies to this tool's result body.</summary>
    public InlineBudgetClass BudgetClass { get; } = budgetClass;
}
