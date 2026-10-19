using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Advanced;

public class DocumentationEngine
{
    private readonly IWorkspaceReader _workspaceManager;

    public DocumentationEngine(IWorkspaceReader workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<DocumentEditResult> GenerateXmlDocumentationStubsAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var normalizedPath = Path.GetFullPath(filePath);
        var document = (solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault()
            ?? solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) &&
                                     string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase))) ?? throw new ToolNotFoundException($"File not found in solution: {normalizedPath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                     && !m.GetLeadingTrivia().Any(t =>
                            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                            t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)));

        // The stub is built with explicit trivia at the method's own indentation instead of re-spacing the whole file;
        // FormatAnnotatedTextAsync then gives the inserted lines the document's dominant line ending.
        var text = await document.GetTextAsync(cancellationToken);
        var newRoot = root.ReplaceNodes(methods, (oldMethod, newMethod) =>
        {
            var line = text.Lines.GetLineFromPosition(oldMethod.SpanStart);
            var linePrefix = text.ToString(TextSpan.FromBounds(line.Start, oldMethod.SpanStart));
            var startsLine = string.IsNullOrWhiteSpace(linePrefix);
            var indentText = linePrefix[..(linePrefix.Length - linePrefix.TrimStart().Length)];

            var sb = new System.Text.StringBuilder();
            void AppendDocLine(string content) => sb.Append(indentText).Append("/// ").Append(content).Append('\n');
            AppendDocLine("<summary>");
            AppendDocLine($"TODO: Add description for {newMethod.Identifier.Text}.");
            AppendDocLine("</summary>");

            foreach (var param in newMethod.ParameterList.Parameters)
            {
                AppendDocLine($"<param name=\"{param.Identifier.Text}\">TODO: Describe {param.Identifier.Text}.</param>");
            }

            if (newMethod.ReturnType.ToString() != "void" && newMethod.ReturnType.ToString() != "Task")
            {
                AppendDocLine("<returns>TODO: Describe return value.</returns>");
            }

            // The declaration's indentation moves after the stub, so stub lines and declaration share it; a method that
            // does not start its line gets the stub on a new line.
            var leading = newMethod.GetLeadingTrivia();
            if (leading.Count > 0 && leading[leading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                leading = leading.RemoveAt(leading.Count - 1);
            }

            if (!startsLine)
            {
                leading = leading.Add(SyntaxFactory.EndOfLine("\n"));
            }

            leading = leading.AddRange(SyntaxFactory.ParseLeadingTrivia(sb.ToString()));
            if (indentText.Length > 0)
            {
                leading = leading.Add(SyntaxFactory.Whitespace(indentText));
            }

            return newMethod.WithLeadingTrivia(leading);
        });

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await AnnotatedInsertionHelper.FormatAnnotatedTextAsync(document, newRoot, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> DocumentPocoFieldsAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Class not found."
            };
        }

        if (!root.Usings.Any(u => u.Name?.ToString() == "System.ComponentModel"))
        {
            root = AnnotatedInsertionHelper.AddUsingDirective(root, "System.ComponentModel");
            classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        }

        var properties = classNode!.Members.OfType<PropertyDeclarationSyntax>().ToList();
        var newClassNode = classNode.ReplaceNodes(properties, (oldProp, newProp) =>
        {
            var attr = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.ParseName("Description"))
                    .WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal($"Gets or sets the {newProp.Identifier.Text}."))))))));
            return AnnotatedInsertionHelper.AppendAttributeLists(newProp, [attr]);
        });

        var newRoot = root.ReplaceNode(classNode, newClassNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await AnnotatedInsertionHelper.FormatAnnotatedTextAsync(document, newRoot, cancellationToken)
        };
    }
}
