using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// Slice D2.1 of docs/current/proposals/proposal_instance_move_preview_position_map_classification.md: the source-to-trial
// position map built from the engine's own TextChanges. The scenarios are built with the real MoveMemberTextEdits helpers
// (the same calls the instance route makes), so the map is exercised against genuine engine edits, not hand-written ones.
[TestFixture]
public class MovePositionMapTests
{
    private static readonly FilePathWrapper PathA = "C:\\x\\A.cs";
    private static readonly FilePathWrapper PathB = "C:\\x\\B.cs";

    private const string ClassAText =
        "namespace N\n" +
        "{\n" +
        "    public class A\n" +
        "    {\n" +
        "        public int Before() => 1;\n" +
        "\n" +
        "        public int Moved()\n" +
        "        {\n" +
        "            return Before();\n" +
        "        }\n" +
        "\n" +
        "        public int After() => 2;\n" +
        "    }\n" +
        "}\n";

    // Members indented by 4 (source members use 8): the moved block is re-indented.
    private const string NarrowTargetText = "public class B\n{\n    public int Existing() => 3;\n}\n";

    // Members indented by 8, like the source: no re-indentation.
    private const string SameIndentTargetText =
        "namespace N\n{\n    public class B\n    {\n        public int Existing() => 3;\n    }\n}\n";

    private sealed record Scenario(
        MovePositionMap Map,
        string AText,
        string BText,
        string TrialA,
        string TrialB,
        Dictionary<FilePathWrapper, string> Trials,
        FilePathWrapper Target);

    // Mirrors AddInstanceMoveStructuralEditsAsync for one member. bText == null puts class B in the same file as class A.
    private static Scenario Build(string aText, string? bText, string memberName, IReadOnlyList<TextChange>? referenceEdits = null)
    {
        var sameFile = bText == null;
        var targetText = bText ?? aText;
        var aRoot = CSharpSyntaxTree.ParseText(aText).GetRoot();
        var member = aRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == memberName);
        var bRoot = sameFile ? aRoot : CSharpSyntaxTree.ParseText(targetText).GetRoot();
        var targetClass = bRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == "B");
        var aSource = SourceText.From(aText);
        var bSource = sameFile ? aSource : SourceText.From(targetText);
        var members = new MemberDeclarationSyntax[] { member };

        var sourceEdits = MoveMemberTextEdits.BuildSourceEdits(aSource, members, referenceEdits ?? Array.Empty<TextChange>(), out var inside);
        var block = MoveMemberTextEdits.BuildMovedMemberText(aSource, member, inside, MoveMemberTextEdits.GetMemberIndentation(aSource, member), MoveMemberTextEdits.GetTargetMemberIndentation(bSource, targetClass), "\n");
        var insertion = MoveMemberTextEdits.BuildInsertion(bSource, targetClass, MoveMemberTextEdits.JoinMovedMembers(members, new[] { block }, "\n"), "\n");

        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>();
        var originals = new Dictionary<FilePathWrapper, SourceText> { [PathA] = aSource };
        var trials = new Dictionary<FilePathWrapper, string>();
        FilePathWrapper target;
        string trialA;
        string trialB;
        if (sameFile)
        {
            sourceEdits.Add(insertion);
            edits[PathA] = sourceEdits;
            trialA = trialB = MoveMemberTextEdits.ApplyChanges(aSource, sourceEdits);
            trials[PathA] = trialA;
            target = PathA;
        }
        else
        {
            edits[PathA] = sourceEdits;
            edits[PathB] = new List<TextChange> { insertion };
            originals[PathB] = bSource;
            trialA = MoveMemberTextEdits.ApplyChanges(aSource, sourceEdits);
            trialB = MoveMemberTextEdits.ApplyChanges(bSource, new[] { insertion });
            trials[PathA] = trialA;
            trials[PathB] = trialB;
            target = PathB;
        }

        var region = new MovedMemberRegion(PathA, member.FullSpan, target, insertion.Span.Start, block, inside);
        var built = MovePositionMap.TryBuild(originals, edits, new[] { region }, out var map, out var error);
        Assert.That(built, Is.True, error);
        return new Scenario(map!, aText, targetText, trialA, trialB, trials, target);
    }

    private static string At(string text, int offset, int length) => text.Substring(offset, length);

    [Test]
    public void UnchangedRegion_BeforeTheRemoval_MapsToItself()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        var offset = s.AText.IndexOf("Before()", StringComparison.Ordinal);

        var result = s.Map.MapOffset(PathA, offset);

        Assert.That(result.IsMapped, Is.True);
        Assert.That(result.Document, Is.EqualTo(PathA));
        Assert.That(result.Offset, Is.EqualTo(offset));
    }

    [Test]
    public void RegionAfterARemoval_IsShiftedByTheRemovedLength()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        var offset = s.AText.IndexOf("After()", StringComparison.Ordinal);

        var result = s.Map.MapOffset(PathA, offset);

        Assert.That(result.IsMapped, Is.True);
        Assert.That(result.Document, Is.EqualTo(PathA));
        Assert.That(result.Offset, Is.LessThan(offset));
        Assert.That(At(s.TrialA, result.Offset, 7), Is.EqualTo("After()"));
    }

    [Test]
    public void RegionAfterAnInsertion_IsShiftedByTheInsertedLength_AndTheBraceMapsAfterTheBlock()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        var existing = s.BText.IndexOf("Existing", StringComparison.Ordinal);
        var closingBrace = s.BText.LastIndexOf('}');

        var before = s.Map.MapOffset(PathB, existing);
        var after = s.Map.MapOffset(PathB, closingBrace);

        Assert.That(before.Offset, Is.EqualTo(existing), "text before the insertion point does not move");
        Assert.That(after.IsMapped, Is.True);
        Assert.That(after.Offset, Is.GreaterThan(closingBrace));
        Assert.That(At(s.TrialB, after.Offset, 1), Is.EqualTo("}"));
        Assert.That(after.Offset, Is.EqualTo(s.TrialB.LastIndexOf('}')), "the brace lands after the inserted block, not inside it");
    }

    [Test]
    public void MovedRegion_MapsAcrossDocuments()
    {
        var s = Build(ClassAText, SameIndentTargetText, "Moved");

        var name = s.Map.MapOffset(PathA, s.AText.IndexOf("Moved()", StringComparison.Ordinal));
        var ret = s.Map.MapOffset(PathA, s.AText.IndexOf("return", StringComparison.Ordinal));

        Assert.That(name.Document, Is.EqualTo(PathB));
        Assert.That(At(s.TrialB, name.Offset, 7), Is.EqualTo("Moved()"));
        Assert.That(ret.Document, Is.EqualTo(PathB));
        Assert.That(At(s.TrialB, ret.Offset, 6), Is.EqualTo("return"));
    }

    [Test]
    public void ReIndentedMovedRegion_KeepsLinesOneToOne_AndShiftsTheColumn()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        var offset = s.AText.IndexOf("return", StringComparison.Ordinal);

        var result = s.Map.MapOffset(PathA, offset);
        var validation = s.Map.Validate(s.Trials);

        Assert.That(At(s.TrialB, result.Offset, 6), Is.EqualTo("return"));
        var sourceColumn = offset - s.AText.LastIndexOf('\n', offset) - 1;
        var destinationColumn = result.Offset - s.TrialB.LastIndexOf('\n', result.Offset) - 1;
        Assert.That((sourceColumn, destinationColumn), Is.EqualTo((12, 8)), "8-space members re-based to 4-space members");
        Assert.That(validation.IsTrusted, Is.True, validation.FirstMismatch?.Describe());
        // Source line 9 ("return Before();") is line 7 of B's trial text; source line 12 ("After") moved up by the five removed lines.
        Assert.That(validation.Lines.MapLine(PathA, 9), Is.EqualTo(LineMapResult.Mapped(PathB, 7)));
        Assert.That(validation.Lines.MapLine(PathA, 12), Is.EqualTo(LineMapResult.Mapped(PathA, 7)));
        Assert.That(validation.Lines.MapLine(PathA, 5), Is.EqualTo(LineMapResult.Mapped(PathA, 5)));
    }

    [Test]
    public void Validation_LineTextDiffers_MarksTheMapUntrustedWithTheFirstMismatch()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        var tampered = new Dictionary<FilePathWrapper, string>(s.Trials)
        {
            [PathB] = s.TrialB.Replace("return Before();", "return Other();", StringComparison.Ordinal),
        };

        var validation = s.Map.Validate(tampered);

        Assert.That(validation.IsTrusted, Is.False);
        Assert.That(validation.FirstMismatch, Is.Not.Null);
        Assert.That(validation.FirstMismatch!.SourceDocument, Is.EqualTo(PathA));
        Assert.That(validation.FirstMismatch.SourceLine, Is.EqualTo(9));
        Assert.That(validation.FirstMismatch.DestinationDocument, Is.EqualTo(PathB));
        Assert.That(validation.FirstMismatch.DestinationLine, Is.EqualTo(7));
        Assert.That(validation.FirstMismatch.Expected, Is.EqualTo("return Before();"));
        Assert.That(validation.FirstMismatch.Actual, Is.EqualTo("return Other();"));
        Assert.That(validation.FirstMismatch.Describe(), Does.Contain("line 9"));
        Assert.That(validation.Lines.MapLine(PathA, 12).Reason, Is.EqualTo(PositionUnmappedReason.MapUntrusted));
    }

    [Test]
    public void Validation_MissingTrialText_IsUntrusted()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");

        var validation = s.Map.Validate(new Dictionary<FilePathWrapper, string> { [PathA] = s.TrialA });

        Assert.That(validation.IsTrusted, Is.False);
        Assert.That(validation.FirstMismatch!.Detail, Does.Contain("no trial text"));
    }

    [Test]
    public void Validation_ALineShiftTheMapDidNotPredict_IsDetected()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        // A rogue extra blank line in the target's trial text shifts the block down by one line.
        var shifted = new Dictionary<FilePathWrapper, string>(s.Trials)
        {
            [PathB] = s.TrialB.Replace("    public int Moved()", "\n    public int Moved()", StringComparison.Ordinal),
        };

        Assert.That(s.Map.Validate(shifted).IsTrusted, Is.False);
    }

    [Test]
    public void UnmappedPositions_ReturnADefinedResult_NotAGuess()
    {
        const string text = "int a = 10;\nint b = 20;\n";
        var original = new Dictionary<FilePathWrapper, SourceText> { [PathA] = SourceText.From(text) };
        var edits = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>> { [PathA] = new List<TextChange> { new(new TextSpan(8, 2), "1000") } };
        Assert.That(MovePositionMap.TryBuild(original, edits, Array.Empty<MovedMemberRegion>(), out var map, out var error), Is.True, error);

        Assert.That(map!.MapOffset(PathA, 8), Is.EqualTo(PositionMapResult.Unmapped(PathA, PositionUnmappedReason.InsideEditedRegion)));
        Assert.That(map.MapOffset(PathA, 9), Is.EqualTo(PositionMapResult.Unmapped(PathA, PositionUnmappedReason.InsideEditedRegion)));
        Assert.That(map.MapOffset(PathA, 10), Is.EqualTo(PositionMapResult.Mapped(PathA, 12)), "the first character after the edit");
        Assert.That(map.MapOffset(PathA, 100), Is.EqualTo(PositionMapResult.Unmapped(PathA, PositionUnmappedReason.OutOfRange)));
        Assert.That(map.MapOffset(PathA, -1).Reason, Is.EqualTo(PositionUnmappedReason.OutOfRange));
        Assert.That(map.MapOffset(PathB, 7), Is.EqualTo(PositionMapResult.Mapped(PathB, 7)), "a document with no edits maps to itself");
    }

    [Test]
    public void UnmappedPosition_InAMovedMembersTrimmedLeadingBlankLine_IsReported()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        // The blank line above Moved belongs to its FullSpan but the engine trims it from the moved block.
        var blankLine = s.AText.IndexOf("\n\n        public int Moved", StringComparison.Ordinal) + 1;

        var result = s.Map.MapOffset(PathA, blankLine);

        Assert.That(result.IsMapped, Is.False);
        Assert.That(result.Reason, Is.EqualTo(PositionUnmappedReason.InsideTrimmedRegion));
    }

    [Test]
    public void ReferenceInsideTheMovedBody_MapsLikeAnyOtherOffset_AbsoluteOrMemberRelative()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        var reference = s.AText.IndexOf("Before();", StringComparison.Ordinal);
        var memberStart = s.AText.IndexOf("\n\n        public int Moved", StringComparison.Ordinal) + 1;

        var absolute = s.Map.MapOffset(PathA, reference);
        var relative = s.Map.MapMovedMemberOffset(0, reference - memberStart);

        Assert.That(absolute.Document, Is.EqualTo(PathB));
        Assert.That(At(s.TrialB, absolute.Offset, 9), Is.EqualTo("Before();"));
        Assert.That(relative, Is.EqualTo(absolute));
        Assert.That(s.Map.MapMovedMemberOffset(0, 100000).Reason, Is.EqualTo(PositionUnmappedReason.OutOfRange));
    }

    [Test]
    public void ReferenceInsideTheMovedBody_WithAnInMemberEdit_LandsAfterTheInsertedQualifier()
    {
        var reference = ClassAText.IndexOf("Before();", StringComparison.Ordinal);
        var qualifier = new TextChange(new TextSpan(reference, 0), "x.");
        var s = Build(ClassAText, NarrowTargetText, "Moved", new[] { qualifier });

        var result = s.Map.MapOffset(PathA, reference);
        var validation = s.Map.Validate(s.Trials);

        Assert.That(s.TrialB, Does.Contain("return x.Before();"));
        Assert.That(At(s.TrialB, result.Offset, 6), Is.EqualTo("Before"));
        Assert.That(validation.IsTrusted, Is.True, validation.FirstMismatch?.Describe());
    }

    [Test]
    public void MoveWithinOneFile_ShiftsByTheRemovalAndMapsTheMemberAcross()
    {
        const string text =
            "namespace N\n" +
            "{\n" +
            "    public class A\n" +
            "    {\n" +
            "        public int Moved()\n" +
            "        {\n" +
            "            return 1;\n" +
            "        }\n" +
            "\n" +
            "        public int Keep() => 2;\n" +
            "    }\n" +
            "\n" +
            "    public class B\n" +
            "    {\n" +
            "        public int Existing() => 3;\n" +
            "    }\n" +
            "}\n";
        var s = Build(text, null, "Moved");

        var keep = s.Map.MapOffset(PathA, text.IndexOf("Keep", StringComparison.Ordinal));
        var existing = s.Map.MapOffset(PathA, text.IndexOf("Existing", StringComparison.Ordinal));
        var ret = s.Map.MapOffset(PathA, text.IndexOf("return", StringComparison.Ordinal));
        var validation = s.Map.Validate(s.Trials);

        Assert.That(At(s.TrialA, keep.Offset, 4), Is.EqualTo("Keep"));
        Assert.That(At(s.TrialA, existing.Offset, 8), Is.EqualTo("Existing"));
        Assert.That(At(s.TrialA, ret.Offset, 6), Is.EqualTo("return"));
        Assert.That(ret.Offset, Is.GreaterThan(existing.Offset), "the member now sits below Existing");
        Assert.That(validation.IsTrusted, Is.True, validation.FirstMismatch?.Describe());
    }

    [Test]
    public void TryBuild_InconsistentInputs_ReturnFalseWithAnError()
    {
        var s = Build(ClassAText, NarrowTargetText, "Moved");
        var original = new Dictionary<FilePathWrapper, SourceText> { [PathA] = SourceText.From(ClassAText) };
        var overlapping = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>
        {
            [PathA] = new List<TextChange> { new(new TextSpan(0, 10), string.Empty), new(new TextSpan(5, 10), string.Empty) },
        };

        Assert.That(MovePositionMap.TryBuild(original, overlapping, Array.Empty<MovedMemberRegion>(), out var map, out var error), Is.False);
        Assert.That(map, Is.Null);
        Assert.That(error, Does.Contain("overlap"));

        var noText = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>> { [PathB] = new List<TextChange> { new(new TextSpan(0, 0), "x") } };
        Assert.That(MovePositionMap.TryBuild(original, noText, Array.Empty<MovedMemberRegion>(), out _, out error), Is.False);
        Assert.That(error, Does.Contain("No original text"));
        Assert.That(s.Map, Is.Not.Null);
    }
}
