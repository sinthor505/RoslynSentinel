using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the MemberRefactoringEngine MoveMember family that moved onto MemberRefactoringNotFound
// (plan_not_found_error_orientation, Step 5c sweep slice C): the base-class resolution of a move to a base type, and the
// other-kind fact of a class miss. Driven through the MoveMember tool; a refused move writes nothing. Facts and options only:
// no policy wording, no bare "not found.".
// Not tested (unreachable by construction, converted for consistency; the factory text itself is covered by MemberRefactoringNotFoundTests):
//  - MoveMemberAsync / PreviewInstanceMoveCallSitesAsync "syntax root" and "semantic model" sites: a document of the loaded
//    solution always yields both.
//  - MoveMembersToBaseTypeAsync "no source file path", "not a document of the loaded solution" and "syntax root" sites: a base
//    type with source is declared in a tree of the solution that has a path, a document and a root.
//  - CollectConversionMoveEditsAsync "target class did not resolve" site: the target class node was just found in that document's
//    own syntax tree, so its declared symbol resolves.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberRefactoringMissMoveTests
{
    private const string FileName = "Move.cs";

    private const string ExternalBaseFixture = """
        public class OrderFailure : System.Exception
        {
            public int Total => 7;
        }
        """;

    // The name 'Dup' is declared twice, as a struct and as an interface, in different containers: never a class.
    private const string OtherKindFixture = """
        public class Holder
        {
            public struct Dup
            {
            }
        }

        public class OtherHolder
        {
            public interface Dup
            {
            }
        }

        public class Target
        {
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
        Assert.That(message, Does.Not.Contain("cannot be modified"), "The old external-assembly policy-style text is gone.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_ToExternalBaseType_StatesTheFactOffersTheOutlineAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, ExternalBaseFixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf(FileName), "OrderFailure", ["Total"], "Exception");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("Base class 'Exception' is declared in an external assembly"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused move must not write the file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_ClassNameDeclaredOnlyAsOtherKinds_NamesEveryOtherKindAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, OtherKindFixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf(FileName), "Dup", ["Total"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("'Dup' is declared in"));
        Assert.That(message, Does.Contain("as a struct and an interface, not as a class."));
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused move must not write the file.");
    }
}
