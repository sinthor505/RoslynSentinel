using System;
using System.Collections.Generic;

namespace RoslynSentinel.Common;

/// <summary>
/// The one shared vocabulary of missingTargetKind strings passed to ToolNotFoundException and
/// MemberRefactoringNotFound. Throw sites use these constants; ErrorPolicyClassifier maps them through
/// the sets below. Every constant belongs to exactly one set, and the unmapped kinds have no error-policy key.
/// </summary>
public static class MissingTargetKinds
{
    /// <summary>File kind: a document in the workspace.</summary>
    public const string Document = "document";

    /// <summary>File kind: a file on disk.</summary>
    public const string File = "file";

    /// <summary>Symbol kind: a class.</summary>
    public const string Class = "class";

    /// <summary>Symbol kind: an interface.</summary>
    public const string Interface = "interface";

    /// <summary>Symbol kind: a generic type.</summary>
    public const string Type = "type";

    /// <summary>Symbol kind: a record.</summary>
    public const string Record = "record";

    /// <summary>Symbol kind: an enum.</summary>
    public const string Enum = "enum";

    /// <summary>Symbol kind: an enum member.</summary>
    public const string EnumMember = "enum member";

    /// <summary>Symbol kind: a method.</summary>
    public const string Method = "method";

    /// <summary>Symbol kind: any member.</summary>
    public const string Member = "member";

    /// <summary>Symbol kind: a declaration of any kind.</summary>
    public const string Declaration = "declaration";

    /// <summary>Symbol kind: a property.</summary>
    public const string Property = "property";

    /// <summary>Symbol kind: a field.</summary>
    public const string Field = "field";

    /// <summary>Symbol kind: a constructor.</summary>
    public const string Constructor = "constructor";

    /// <summary>Symbol kind: a parameter.</summary>
    public const string Parameter = "parameter";

    /// <summary>Symbol kind: an attribute.</summary>
    public const string Attribute = "attribute";

    /// <summary>Symbol kind: a namespace.</summary>
    public const string Namespace = "namespace";

    /// <summary>Symbol kind: a symbol of unspecified kind.</summary>
    public const string Symbol = "symbol";

    /// <summary>Symbol kind: a container (type or namespace).</summary>
    public const string Container = "container";

    /// <summary>Symbol kind: a using directive.</summary>
    public const string UsingDirective = "using directive";

    /// <summary>Unmapped kind: a project.</summary>
    public const string Project = "project";

    /// <summary>Unmapped kind: a statement.</summary>
    public const string Statement = "statement";

    /// <summary>Unmapped kind: a list of statements.</summary>
    public const string Statements = "statements";

    /// <summary>Unmapped kind: an expression.</summary>
    public const string Expression = "expression";

    /// <summary>Unmapped kind: a code snippet.</summary>
    public const string Snippet = "snippet";

    /// <summary>Unmapped kind: a block.</summary>
    public const string Block = "block";

    /// <summary>Unmapped kind: a method body.</summary>
    public const string MethodBody = "method body";

    /// <summary>Unmapped kind: a switch statement.</summary>
    public const string SwitchStatement = "switch statement";

    /// <summary>Kinds that map to <see cref="ErrorPolicyTargetKind.File"/>.</summary>
    public static readonly IReadOnlySet<string> FileKinds =
        new HashSet<string>(new[] { Document, File }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Kinds that map to <see cref="ErrorPolicyTargetKind.Symbol"/>.</summary>
    public static readonly IReadOnlySet<string> SymbolKinds = new HashSet<string>(
        new[]
        {
            Class, Interface, Type, Record, Enum, EnumMember, Method, Member, Declaration, Property,
            Field, Constructor, Parameter, Attribute, Namespace, Symbol, Container, UsingDirective,
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Kinds deliberately without an error-policy key; ErrorPolicyClassifier returns null for them.</summary>
    public static readonly IReadOnlySet<string> UnmappedKinds = new HashSet<string>(
        new[]
        {
            Project, Statement, Statements, Expression, Snippet, Block, MethodBody, SwitchStatement,
        },
        StringComparer.OrdinalIgnoreCase);
}
