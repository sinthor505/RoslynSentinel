// In claude-lean, ParameterEdit replaces MethodSignature + ConstructorParameter and Declaration replaces
// ModifyModifier + ChangeAccessibility + ModifyAttribute + ModifyBaseType. Their description text is single-sourced in
// ToolParams (see the "Merged-tool shared text" block there); these tests fail if a merged tool and a source tool stop
// emitting the same shared text, which is what a hand-edit of one side only would cause.
//   1. Params whose whole description is shared must be byte-identical in both emitted schemas.
//   2. Params whose description is intentionally different must still both contain the shared clause.
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("ParameterEditTools")]
[Category("DeclarationTools")]
public class MergedToolDescriptionSyncTests
{
    // A tool or parameter in the emitted schema; Param null means the tool-level description.
    public readonly record struct Slot(Type Type, string Method, string? Param)
    {
        public override string ToString() => Param is null ? $"{Method} (tool description)" : $"{Method}.{Param}";
    }

    private static Slot Tool(Type type, string method) => new(type, method, null);

    private static Slot Param(Type type, string method, string param) => new(type, method, param);

    private static readonly Type Pe = typeof(ParameterEditTools);
    private static readonly Type Sig = typeof(RefactoringSignatureTools);
    private static readonly Type Decl = typeof(DeclarationTools);
    private static readonly Type Struct = typeof(RefactoringStructuralTools);

    private static string Emitted(Slot slot)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        string json = JsonSerializer.Serialize(
            McpToolSchemaPatcher.CreateSentinelTool(slot.Type.GetMethod(slot.Method)!, slot.Type, services).ProtocolTool,
            ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (slot.Param is null)
        {
            return root.GetProperty("description").GetString() ?? string.Empty;
        }

        var property = root.GetProperty("inputSchema").GetProperty("properties").GetProperty(slot.Param);
        return property.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty;
    }

    // ---- 1. whole description shared: the two emitted strings must be equal ----

    public static IEnumerable<TestCaseData> IdenticalDescriptionCases()
    {
        // Params every merged tool shares with each of its source tools by name.
        foreach (var name in new[] { "reason", "contextSnippet", "lineBefore", "lineAfter", "autoStage", "dryRun", "returnDiff" })
        {
            string constant = name switch
            {
                "reason" => nameof(ToolParams.Reason),
                "contextSnippet" => nameof(ToolParams.ContextSnippet),
                "lineBefore" => nameof(ToolParams.LineBefore),
                "lineAfter" => nameof(ToolParams.LineAfter),
                "autoStage" => nameof(ToolParams.AutoStage),
                "dryRun" => nameof(ToolParams.DryRun),
                _ => nameof(ToolParams.ReturnDiff),
            };

            yield return Identical(Param(Pe, "ParameterEdit", name), Param(Sig, "MethodSignature", name), constant);
            yield return Identical(Param(Pe, "ParameterEdit", name), Param(Sig, "ConstructorParameter", name), constant);
            yield return Identical(Param(Decl, "Declaration", name), Param(Struct, "ModifyModifier", name), constant);
            yield return Identical(Param(Decl, "Declaration", name), Param(Sig, "ChangeAccessibility", name), constant);
            yield return Identical(Param(Decl, "Declaration", name), Param(Struct, "ModifyAttribute", name), constant);
            yield return Identical(Param(Decl, "Declaration", name), Param(Struct, "ModifyBaseType", name), constant);
        }

        yield return Identical(Param(Decl, "Declaration", "accessibility"), Param(Sig, "ChangeAccessibility", "accessibility"), nameof(ToolParams.AccessibilityValues));
    }

    private static TestCaseData Identical(Slot merged, Slot source, string constant) =>
        new TestCaseData(merged, source, constant).SetName($"Identical_{merged}_vs_{source}");

    [TestCaseSource(nameof(IdenticalDescriptionCases))]
    public void SharedParameterDescription_IsIdenticalInMergedAndSourceTool(Slot merged, Slot source, string constant)
    {
        string mergedText = Emitted(merged);
        string sourceText = Emitted(source);

        Assert.That(mergedText, Is.Not.Empty, $"{merged} emitted no description.");
        Assert.That(
            mergedText,
            Is.EqualTo(sourceText),
            $"{merged} and {source} must emit the same description. Edit the single shared constant ToolParams.{constant} " +
            "(RoslynSentinel.Common/ToolParams.cs) and make both tools reference it; do not hand-edit one tool's text.");
    }

    // ---- 2. intentionally different wrapper text: both must still carry the shared clause ----

    public static IEnumerable<TestCaseData> SharedClauseCases()
    {
        var clauses = new (Slot Merged, Slot Source, string Name, string Value)[]
        {
            // ParameterEdit <-> MethodSignature
            (Param(Pe, "ParameterEdit", "operation"), Param(Sig, "MethodSignature", "operation"), nameof(ToolParams.RemoveLastParameter), ToolParams.RemoveLastParameter),
            (Param(Pe, "ParameterEdit", "defaultValue"), Param(Sig, "MethodSignature", "defaultValue"), nameof(ToolParams.DefaultValueLead), ToolParams.DefaultValueLead),
            (Param(Pe, "ParameterEdit", "defaultValue"), Param(Sig, "MethodSignature", "defaultValue"), nameof(ToolParams.DefaultValueTail), ToolParams.DefaultValueTail),
            (Param(Pe, "ParameterEdit", "nullDefault"), Param(Sig, "MethodSignature", "nullDefault"), nameof(ToolParams.NullDefaultLead), ToolParams.NullDefaultLead),
            (Param(Pe, "ParameterEdit", "nullDefault"), Param(Sig, "MethodSignature", "nullDefault"), nameof(ToolParams.NullDefaultTail), ToolParams.NullDefaultTail),

            // ParameterEdit <-> ConstructorParameter
            (Param(Pe, "ParameterEdit", "defaultValue"), Param(Sig, "ConstructorParameter", "defaultValue"), nameof(ToolParams.DefaultValueLead), ToolParams.DefaultValueLead),
            (Param(Pe, "ParameterEdit", "defaultValue"), Param(Sig, "ConstructorParameter", "defaultValue"), nameof(ToolParams.DefaultValueTail), ToolParams.DefaultValueTail),
            (Param(Pe, "ParameterEdit", "nullDefault"), Param(Sig, "ConstructorParameter", "nullDefault"), nameof(ToolParams.NullDefaultLead), ToolParams.NullDefaultLead),
            (Param(Pe, "ParameterEdit", "nullDefault"), Param(Sig, "ConstructorParameter", "nullDefault"), nameof(ToolParams.NullDefaultTail), ToolParams.NullDefaultTail),
            (Param(Pe, "ParameterEdit", "fieldName"), Param(Sig, "ConstructorParameter", "fieldName"), nameof(ToolParams.FieldNameOverride), ToolParams.FieldNameOverride),
            (Param(Pe, "ParameterEdit", "callSiteFixups"), Param(Sig, "ConstructorParameter", "callSiteFixups"), nameof(ToolParams.CallSiteTargets), ToolParams.CallSiteTargets),
            (Param(Pe, "ParameterEdit", "callSiteFixups"), Param(Sig, "ConstructorParameter", "callSiteFixups"), nameof(ToolParams.CallSiteKeys), ToolParams.CallSiteKeys),
            (Param(Pe, "ParameterEdit", "callSiteFixups"), Param(Sig, "ConstructorParameter", "callSiteFixups"), nameof(ToolParams.CallSiteFallback), ToolParams.CallSiteFallback),

            // Declaration <-> ModifyModifier / ModifyAttribute / ModifyBaseType
            (Tool(Decl, "Declaration"), Tool(Struct, "ModifyModifier"), nameof(ToolParams.AddStaticUnsupported), ToolParams.AddStaticUnsupported),
            (Param(Decl, "Declaration", "modifierBatchEdits"), Param(Struct, "ModifyModifier", "batchEdits"), nameof(ToolParams.ModifierEdits), ToolParams.ModifierEdits),
            (Param(Decl, "Declaration", "existingAttribute"), Param(Struct, "ModifyAttribute", "existingAttribute"), nameof(ToolParams.AttributeNameClause), ToolParams.AttributeNameClause),
            (Param(Decl, "Declaration", "newAttribute"), Param(Struct, "ModifyAttribute", "newAttribute"), nameof(ToolParams.NewAttributeClause), ToolParams.NewAttributeClause),
            (Param(Decl, "Declaration", "attributeBatchEdits"), Param(Struct, "ModifyAttribute", "batchEdits"), nameof(ToolParams.AttributeEdits), ToolParams.AttributeEdits),
            (Param(Decl, "Declaration", "baseTypeName"), Param(Struct, "ModifyBaseType", "baseTypeName"), nameof(ToolParams.BaseTypeNameClause), ToolParams.BaseTypeNameClause),
            (Param(Decl, "Declaration", "baseTypeBatchEdits"), Param(Struct, "ModifyBaseType", "batchEdits"), nameof(ToolParams.BaseTypeEdits), ToolParams.BaseTypeEdits),
        };

        foreach (var (merged, source, name, value) in clauses)
        {
            yield return new TestCaseData(merged, source, name, value).SetName($"Clause_{name}_in_{merged}_and_{source}");
        }
    }

    [TestCaseSource(nameof(SharedClauseCases))]
    public void SharedClause_IsEmittedByBothMergedAndSourceTool(Slot merged, Slot source, string constantName, string clause)
    {
        string hint = $"Edit the single shared constant ToolParams.{constantName} (RoslynSentinel.Common/ToolParams.cs) and make both " +
            "tools reference it; do not hand-edit one tool's text.";

        Assert.That(Emitted(merged), Does.Contain(clause), $"{merged} no longer emits ToolParams.{constantName}. {hint}");
        Assert.That(Emitted(source), Does.Contain(clause), $"{source} no longer emits ToolParams.{constantName}. {hint}");
    }
}
