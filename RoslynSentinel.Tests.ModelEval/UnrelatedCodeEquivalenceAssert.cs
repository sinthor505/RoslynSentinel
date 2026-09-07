using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Fallback check for "unrelated code unchanged" when a member has no dedicated behavior test
/// (front-door or direct) to prove it — see
/// docs/current/modeleval_fixture_test_suite_redesign.md. Compares the member's syntax structure
/// via <see cref="SyntaxFactory.AreEquivalent(SyntaxNode?, SyntaxNode?, bool)"/> with
/// <c>topLevel: false</c>, so trivia/whitespace differences (legitimate reformatting) don't count
/// as a change — only this. Not a primary check: prefer a real test against the member wherever
/// one is reachable, since a passing behavior test is strictly better evidence than structural
/// equivalence of source that was never exercised.
/// </summary>
internal static class UnrelatedCodeEquivalenceAssert
{
    /// <summary>
    /// Locates the member named <paramref name="memberName"/> in <paramref name="filePath"/> and
    /// asserts it's structurally equivalent (ignoring trivia) to <paramref name="expectedMemberSource"/>,
    /// which must itself be parseable as a single member declaration (e.g. a method or property,
    /// wrapped in enough of a class/namespace shell to parse — see overload note below). Throws
    /// with a diagnostic message showing both member sources on mismatch, or if the member can't
    /// be found in either source.
    /// </summary>
    public static void AssertMemberUnchanged(string filePath, string memberName, string expectedMemberSource)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"UnrelatedCodeEquivalenceAssert: no file at '{filePath}' to check member '{memberName}'.", filePath);
        }

        var actualText = File.ReadAllText(filePath);
        var actualMember = FindMember(actualText, memberName)
            ?? throw new InvalidOperationException(
                $"UnrelatedCodeEquivalenceAssert: member '{memberName}' not found in '{filePath}' — " +
                "the model's edit may have renamed or removed it.");
        var expectedMember = FindMember(expectedMemberSource, memberName)
            ?? throw new InvalidOperationException(
                $"UnrelatedCodeEquivalenceAssert: member '{memberName}' not found in the expected source " +
                "passed to this assertion — check the fixture's expected snippet.");

        if (!SyntaxFactory.AreEquivalent(actualMember, expectedMember, topLevel: false))
        {
            throw new InvalidOperationException(
                $"UnrelatedCodeEquivalenceAssert: member '{memberName}' in '{filePath}' changed structurally " +
                $"(trivia-insensitive comparison).\n--- expected ---\n{expectedMember}\n--- actual ---\n{actualMember}");
        }
    }

    private static MemberDeclarationSyntax? FindMember(string source, string memberName)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == memberName);
    }

    private static string? GetMemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.Text,
        PropertyDeclarationSyntax property => property.Identifier.Text,
        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };
}
