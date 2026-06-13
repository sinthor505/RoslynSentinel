namespace RoslynSentinel.Common;

public static class ToolParams
{
    public const string SessionId =
        "Session ID returned by load_solution. Used to detect workspace reload. " +
        "Pass the value exactly as returned — do not construct or modify.";

    public const string ProjectName =
        "Project name returned by locate_symbol in the projectName field. " +
        "Must match exactly — case-sensitive.";

    public const string DocCommentId =
        "Documentation comment ID returned by locate_symbol in the docCommentId field. " +
        "Uniquely identifies the symbol across tool calls. " +
        "Do not construct this value — pass it exactly as returned by locate_symbol.";
}
