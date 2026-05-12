using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for CloneDetectionEngine, OutParamRefactoringEngine,
/// and the two new AntiPatternEngine methods added in this sprint.
/// </summary>
[TestFixture]
public class NewFeaturesTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private CloneDetectionEngine _cloneEngine;
    private OutParamRefactoringEngine _outParamEngine;
    private AntiPatternEngine _antiPatternEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _cloneEngine = new CloneDetectionEngine(_workspaceManager);
        _outParamEngine = new OutParamRefactoringEngine(_workspaceManager);
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CloneDetectionEngine — FindDuplicateBlocksInClassAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CloneInClass_DetectsDuplicateBlocks_AcrossMethodsInSameClass()
    {
        // Two methods with the same 4-statement body shape — should detect one group with 2 occurrences.
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = 2;
        int z = x + y;
        System.Console.WriteLine(z);
    }

    public void Beta()
    {
        int a = 1;
        int b = 2;
        int c = a + b;
        System.Console.WriteLine(c);
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 4);

        Assert.That(groups, Is.Not.Empty, "Expected at least one duplicate group");
        var group = groups.First();
        Assert.That(group.Occurrences.Count, Is.EqualTo(2), "Both methods should appear as occurrences");
        Assert.That(group.StatementCount, Is.EqualTo(4));
    }

    [Test]
    public async Task CloneInClass_NoDuplicates_WhenMethodBodiesDiffer()
    {
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = x + 2;
        System.Console.WriteLine(y);
    }

    public void Beta()
    {
        string s = ""hello"";
        int len = s.Length;
        System.Console.WriteLine(len);
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 3);

        Assert.That(groups, Is.Empty);
    }

    [Test]
    public async Task CloneInClass_RespectsMinStatements_DoesNotFlagSmallerBlocks()
    {
        // Two methods with 2 identical statements — below the threshold of 4.
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = x + 2;
    }

    public void Beta()
    {
        int a = 1;
        int b = a + 2;
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 4);

        Assert.That(groups, Is.Empty, "Should not flag blocks smaller than minStatements");
    }

    [Test]
    public async Task CloneInClass_RespectsMinStatements_FlagsBlocksAtThreshold()
    {
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = x + 2;
    }

    public void Beta()
    {
        int a = 1;
        int b = a + 2;
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 2);

        Assert.That(groups, Is.Not.Empty, "Should flag blocks at minStatements=2");
    }

    [Test]
    public async Task CloneInClass_HasControlFlowExit_TrueWhenReturnInBlock()
    {
        SetSource(@"
public class MyService
{
    public int Alpha()
    {
        int x = 1;
        int y = x + 2;
        if (y > 0) return y;
        return 0;
    }

    public int Beta()
    {
        int a = 1;
        int b = a + 2;
        if (b > 0) return b;
        return 0;
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 3);

        // At least one group should have HasControlFlowExit = true
        var exitGroup = groups.FirstOrDefault(g => g.HasControlFlowExit);
        Assert.That(exitGroup, Is.Not.Null, "Should detect control flow exit in clone block");
    }

    [Test]
    public async Task CloneInClass_HasControlFlowExit_FalseWhenNoReturn()
    {
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = x + 2;
        int z = y * 3;
        System.Console.WriteLine(z);
    }

    public void Beta()
    {
        int a = 1;
        int b = a + 2;
        int c = b * 3;
        System.Console.WriteLine(c);
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 4);

        Assert.That(groups, Is.Not.Empty);
        Assert.That(groups[0].HasControlFlowExit, Is.False);
    }

    [Test]
    public async Task CloneInClass_ReturnsEmpty_WhenFileNotFound()
    {
        SetSource("public class C { public void M() { int x = 1; } }", "C.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("DoesNotExist.cs", "C", minStatements: 2);

        Assert.That(groups, Is.Empty);
    }

    [Test]
    public async Task CloneInClass_ReturnsEmpty_WhenClassNotFound()
    {
        SetSource("public class C { public void M() { int x = 1; } }", "C.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("C.cs", "NonExistentClass", minStatements: 2);

        Assert.That(groups, Is.Empty);
    }

    [Test]
    public async Task CloneInClass_OccurrencesHaveCorrectMethodNames()
    {
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = x + 2;
        int z = y * 3;
        System.Console.WriteLine(z);
    }

    public void Beta()
    {
        int a = 1;
        int b = a + 2;
        int c = b * 3;
        System.Console.WriteLine(c);
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 4);

        Assert.That(groups, Is.Not.Empty);
        var methodNames = groups[0].Occurrences.Select(o => o.MethodName).ToHashSet();
        Assert.That(methodNames, Does.Contain("Alpha"));
        Assert.That(methodNames, Does.Contain("Beta"));
    }

    [Test]
    public async Task CloneInClass_OccurrencesHaveValidLineNumbers()
    {
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = x + 2;
        int z = y * 3;
        System.Console.WriteLine(z);
    }

    public void Beta()
    {
        int a = 1;
        int b = a + 2;
        int c = b * 3;
        System.Console.WriteLine(c);
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 4);

        Assert.That(groups, Is.Not.Empty);
        foreach (var occ in groups[0].Occurrences)
        {
            Assert.That(occ.StartLine, Is.GreaterThan(0));
            Assert.That(occ.EndLine, Is.GreaterThanOrEqualTo(occ.StartLine));
        }
    }

    [Test]
    public async Task CloneInClass_SnippetPreview_IsPopulated()
    {
        SetSource(@"
public class MyService
{
    public void Alpha()
    {
        int x = 1;
        int y = x + 2;
        int z = y * 3;
        System.Console.WriteLine(z);
    }

    public void Beta()
    {
        int a = 1;
        int b = a + 2;
        int c = b * 3;
        System.Console.WriteLine(c);
    }
}", "MyService.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInClassAsync("MyService.cs", "MyService", minStatements: 4);

        Assert.That(groups, Is.Not.Empty);
        Assert.That(groups[0].SnippetPreview, Is.Not.Null.And.Not.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CloneDetectionEngine — FindDuplicateBlocksInHierarchyAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CloneInHierarchy_DetectsClones_AcrossDerivedClasses()
    {
        SetMultipleFiles(
            ("Base.cs", @"
public abstract class Base
{
    public virtual void OnStart() { }
}"),
            ("Child1.cs", @"
public class Child1 : Base
{
    public void ProcessData()
    {
        int x = 1;
        int y = x + 2;
        int z = y * 3;
        System.Console.WriteLine(z);
    }
}"),
            ("Child2.cs", @"
public class Child2 : Base
{
    public void ProcessData()
    {
        int a = 1;
        int b = a + 2;
        int c = b * 3;
        System.Console.WriteLine(c);
    }
}"));

        var groups = await _cloneEngine.FindDuplicateBlocksInHierarchyAsync("Base", minStatements: 4);

        Assert.That(groups, Is.Not.Empty, "Should detect clones across derived classes");
        var typeNames = groups[0].Occurrences.Select(o => o.ContainingType).ToHashSet();
        Assert.That(typeNames.Count, Is.GreaterThan(1), "Occurrences should span multiple types");
    }

    [Test]
    public async Task CloneInHierarchy_ReturnsEmpty_WhenTypeNotFound()
    {
        SetSource("public class C { public void M() { int x = 1; } }", "C.cs");

        var groups = await _cloneEngine.FindDuplicateBlocksInHierarchyAsync("DoesNotExist", minStatements: 2);

        Assert.That(groups, Is.Empty);
    }

    [Test]
    public async Task CloneInHierarchy_NoDuplicates_WhenDerivedMethodsDiffer()
    {
        SetMultipleFiles(
            ("Base.cs", "public abstract class Base { }"),
            ("Child1.cs", @"
public class Child1 : Base
{
    public void DoA()
    {
        int x = 1;
        string s = x.ToString();
        System.Console.WriteLine(s);
    }
}"),
            ("Child2.cs", @"
public class Child2 : Base
{
    public void DoB()
    {
        double d = 3.14;
        int r = (int)d;
        System.Console.WriteLine(r);
    }
}"));

        var groups = await _cloneEngine.FindDuplicateBlocksInHierarchyAsync("Base", minStatements: 3);

        Assert.That(groups, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AntiPatternEngine — FindMultipleOutParameterMethodsAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FindMultipleOutParams_DetectsMethodWithTwoOutParams()
    {
        SetSource(@"
public class MyParser
{
    public void Parse(string input, out int number, out bool success)
    {
        success = int.TryParse(input, out number);
    }
}", "MyParser.cs");

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("MyParser.cs");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].MethodName, Is.EqualTo("Parse"));
        Assert.That(findings[0].OutParamNames, Does.Contain("number"));
        Assert.That(findings[0].OutParamNames, Does.Contain("success"));
    }

    [Test]
    public async Task FindMultipleOutParams_DoesNotFlag_MethodWithOnlyOneOutParam()
    {
        SetSource(@"
public class MyService
{
    public bool TryGet(string key, out string value)
    {
        value = ""test"";
        return true;
    }
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("MyService.cs");

        Assert.That(findings, Is.Empty, "Single out param should not be flagged");
    }

    [Test]
    public async Task FindMultipleOutParams_DoesNotFlag_MethodWithNoOutParams()
    {
        SetSource(@"
public class MyService
{
    public string Process(string input, int count) => input.Substring(0, count);
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("MyService.cs");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public async Task FindMultipleOutParams_SuggestsCorrectTuple_ForVoidReturn()
    {
        SetSource(@"
public class Parser
{
    public void Parse(string s, out int num, out bool ok)
    {
        ok = int.TryParse(s, out num);
    }
}", "Parser.cs");

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("Parser.cs");

        Assert.That(findings, Is.Not.Empty);
        // Void method: suggested tuple should have both out params but no extra 'result' prefix
        Assert.That(findings[0].SuggestedTupleReturn, Does.Contain("num"));
        Assert.That(findings[0].SuggestedTupleReturn, Does.Contain("ok"));
        Assert.That(findings[0].CurrentReturnType, Is.EqualTo("void"));
    }

    [Test]
    public async Task FindMultipleOutParams_SuggestsCorrectTuple_ForNonVoidReturn()
    {
        SetSource(@"
public class Parser
{
    public bool TryParseMultiple(string s, out int num, out string text)
    {
        num = 0;
        text = s;
        return true;
    }
}", "Parser.cs");

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("Parser.cs");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].CurrentReturnType, Is.EqualTo("bool"));
        // Non-void: tuple should include the original return type as 'result'
        Assert.That(findings[0].SuggestedTupleReturn, Does.Contain("result"));
        Assert.That(findings[0].SuggestedTupleReturn, Does.Contain("num"));
        Assert.That(findings[0].SuggestedTupleReturn, Does.Contain("text"));
    }

    [Test]
    public async Task FindMultipleOutParams_DetectsThreeOutParams()
    {
        SetSource(@"
public class MultiOut
{
    public void GetAll(string input, out int a, out string b, out bool c)
    {
        a = 1; b = input; c = true;
    }
}", "MultiOut.cs");

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("MultiOut.cs");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].OutParamNames.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task FindMultipleOutParams_PopulatesContainingType()
    {
        SetSource(@"
public class MyClass
{
    public void M(out int x, out int y) { x = 1; y = 2; }
}", "MyClass.cs");

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("MyClass.cs");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].ContainingType, Is.EqualTo("MyClass"));
    }

    [Test]
    public async Task FindMultipleOutParams_ScansWholeProject_WhenNoFileSpecified()
    {
        SetMultipleFiles(
            ("A.cs", "public class A { public void M(out int x, out int y) { x=1; y=2; } }"),
            ("B.cs", "public class B { public void N(out string a, out string b) { a=\"\"; b=\"\"; } }"));

        var findings = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync();

        Assert.That(findings.Count, Is.EqualTo(2), "Both files should be scanned");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // AntiPatternEngine — FindValueTypeMutationIntentAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ValueTypeMutation_Flags_IntParamReassignedNotReturned()
    {
        SetSource(@"
public class Calculator
{
    public void Double(int value)
    {
        value = value * 2;
    }
}", "Calculator.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("Calculator.cs");

        Assert.That(findings, Is.Not.Empty);
        var finding = findings.First(f => f.Pattern == "ValueTypeParameterReassigned");
        Assert.That(finding.Description, Does.Contain("value"));
    }

    [Test]
    public async Task ValueTypeMutation_Flags_BoolParamReassignedNotReturned()
    {
        SetSource(@"
public class MyService
{
    public void Toggle(bool flag)
    {
        flag = !flag;
    }
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("MyService.cs");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Pattern, Is.EqualTo("ValueTypeParameterReassigned"));
    }

    [Test]
    public async Task ValueTypeMutation_DoesNotFlag_WhenValueTypeIsReturned()
    {
        SetSource(@"
public class Calculator
{
    public int Double(int value)
    {
        value = value * 2;
        return value;
    }
}", "Calculator.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("Calculator.cs");

        Assert.That(findings, Is.Empty, "Should not flag when the reassigned value is returned");
    }

    [Test]
    public async Task ValueTypeMutation_DoesNotFlag_RefParam()
    {
        SetSource(@"
public class MyService
{
    public void Increment(ref int value)
    {
        value = value + 1;
    }
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("MyService.cs");

        Assert.That(findings, Is.Empty, "ref params are intentionally pass-by-reference — should not be flagged");
    }

    [Test]
    public async Task ValueTypeMutation_DoesNotFlag_OutParam()
    {
        SetSource(@"
public class MyService
{
    public bool TryGet(out int value)
    {
        value = 42;
        return true;
    }
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("MyService.cs");

        Assert.That(findings, Is.Empty, "out params are intentionally pass-by-reference — should not be flagged");
    }

    [Test]
    public async Task ValueTypeMutation_DoesNotFlag_MemberAccessAssignment()
    {
        // param.Property = value — this IS visible to the caller (mutating the object's state)
        SetSource(@"
public class Dto
{
    public string Name { get; set; } = """";
}
public class MyService
{
    public void SetName(Dto dto, string name)
    {
        dto.Name = name;
    }
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("MyService.cs");

        // Member access assignment is NOT a reassignment of the parameter reference itself
        Assert.That(findings.Any(f => f.Pattern == "ReferenceTypeParameterReplaced"), Is.False,
            "dto.Name = ... is a member mutation, not a reference replacement");
    }

    [Test]
    public async Task ValueTypeMutation_Flags_ReferenceTypeParamReplacedWithNew()
    {
        SetSource(@"
public class MyService
{
    public void Process(System.Collections.Generic.List<string> items)
    {
        items = new System.Collections.Generic.List<string>();
    }
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("MyService.cs");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Pattern, Is.EqualTo("ReferenceTypeParameterReplaced"));
        Assert.That(findings[0].Description, Does.Contain("items"));
    }

    [Test]
    public async Task ValueTypeMutation_DoesNotFlag_ReferenceTypeSimpleAssignment_NonNew()
    {
        // Assigning from another variable, not a 'new' expression — ambiguous intent, don't flag
        SetSource(@"
public class MyService
{
    private System.Collections.Generic.List<string> _default = new();

    public void Process(System.Collections.Generic.List<string> items)
    {
        items = _default;
    }
}", "MyService.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("MyService.cs");

        // We only flag `= new ...`, not arbitrary reassignments of reference types
        Assert.That(findings.Any(f => f.Pattern == "ReferenceTypeParameterReplaced"), Is.False,
            "Non-new assignment of reference type param should not be flagged");
    }

    [Test]
    public async Task ValueTypeMutation_SeverityIsWarning()
    {
        SetSource(@"
public class C
{
    public void M(int x) { x = 5; }
}", "C.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("C.cs");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings[0].Severity, Is.EqualTo("Warning"));
    }

    [Test]
    public async Task ValueTypeMutation_ScansWholeProject_WhenNoFileSpecified()
    {
        SetMultipleFiles(
            ("A.cs", "public class A { public void M(int x) { x = 5; } }"),
            ("B.cs", "public class B { public void N(bool flag) { flag = !flag; } }"));

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync();

        Assert.That(findings.Count, Is.GreaterThanOrEqualTo(2), "Both files should be scanned");
    }

    [Test]
    public async Task ValueTypeMutation_ReturnsEmpty_WhenNoIssues()
    {
        SetSource(@"
public class Clean
{
    public int Double(int x) => x * 2;
    public void Log(string msg) => System.Console.WriteLine(msg);
}", "Clean.cs");

        var findings = await _antiPatternEngine.FindValueTypeMutationIntentAsync("Clean.cs");

        Assert.That(findings, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // OutParamRefactoringEngine — ConvertOutParamsToValueTupleAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConvertOutParams_ReturnsFailure_WhenFileNotFound()
    {
        SetSource("public class C { public void M(out int x, out int y) { x=1; y=2; } }", "C.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("DoesNotExist.cs", "M");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found").IgnoreCase);
    }

    [Test]
    public async Task ConvertOutParams_ReturnsFailure_WhenMethodNotFound()
    {
        SetSource("public class C { public void M(out int x, out int y) { x=1; y=2; } }", "C.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("C.cs", "NonExistentMethod");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found").IgnoreCase);
    }

    [Test]
    public async Task ConvertOutParams_ReturnsFailure_WhenFewerThanTwoOutParams()
    {
        SetSource(@"
public class C
{
    public bool TryGet(string key, out string value)
    {
        value = key;
        return true;
    }
}", "C.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("C.cs", "TryGet");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("fewer than 2").IgnoreCase);
    }

    [Test]
    public async Task ConvertOutParams_Succeeds_VoidMethodWithTwoOutParams()
    {
        SetSource(@"
public class MyParser
{
    public void Parse(string input, out int number, out bool success)
    {
        success = int.TryParse(input, out number);
    }
}", "MyParser.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("MyParser.cs", "Parse");

        Assert.That(result.Success, Is.True, $"Expected success but got: {result.Message}");
        Assert.That(result.NewSignature, Does.Contain("number"));
        Assert.That(result.NewSignature, Does.Contain("success"));
        Assert.That(result.OriginalSignature, Does.Contain("out int number"));
    }

    [Test]
    public async Task ConvertOutParams_Succeeds_NonVoidMethodWithTwoOutParams()
    {
        SetSource(@"
public class MyParser
{
    public bool TryParse(string input, out int number, out string formatted)
    {
        if (int.TryParse(input, out number))
        {
            formatted = number.ToString(""N0"");
            return true;
        }
        formatted = string.Empty;
        return false;
    }
}", "MyParser.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("MyParser.cs", "TryParse");

        Assert.That(result.Success, Is.True, $"Expected success but got: {result.Message}");
        // Non-void return: new signature should contain the original return type + out param types
        Assert.That(result.NewSignature, Does.Contain("number").Or.Contain("formatted"));
    }

    [Test]
    public async Task ConvertOutParams_PopulatesOriginalSignature()
    {
        SetSource(@"
public class C
{
    public void M(int a, out int x, out bool y) { x = a; y = true; }
}", "C.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("C.cs", "M");

        Assert.That(result.OriginalSignature, Is.Not.Null.And.Not.Empty);
        Assert.That(result.OriginalSignature, Does.Contain("out"));
    }

    [Test]
    public async Task ConvertOutParams_ZeroCallSitesRewritten_WhenNoCallers()
    {
        // Method exists but nothing calls it — call sites = 0 is expected
        SetSource(@"
public class C
{
    public void M(out int x, out int y) { x = 1; y = 2; }
}", "C.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("C.cs", "M");

        Assert.That(result.Success, Is.True, $"Expected success but got: {result.Message}");
        Assert.That(result.CallSitesRewritten, Is.EqualTo(0), "No callers in this solution");
    }

    [Test]
    public async Task ConvertOutParams_CallSiteWarnings_InitiallyEmpty_WhenNoCallers()
    {
        SetSource(@"
public class C
{
    public void M(out int x, out int y) { x = 1; y = 2; }
}", "C.cs");

        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync("C.cs", "M");

        Assert.That(result.Success, Is.True);
        Assert.That(result.CallSiteWarnings, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Integration: FindMultipleOutParams → ConvertOutParamsToValueTuple workflow
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Workflow_DetectThenConvert_Roundtrip()
    {
        // Detect with FindMultipleOutParameterMethodsAsync, then convert using the result.
        SetSource(@"
public class Parser
{
    public void Parse(string input, out int num, out bool ok)
    {
        ok = int.TryParse(input, out num);
    }
}", "Parser.cs");

        // Step 1: detect
        var detections = await _antiPatternEngine.FindMultipleOutParameterMethodsAsync("Parser.cs");
        Assert.That(detections, Is.Not.Empty, "Detection step should find the method");

        var detection = detections[0];
        Assert.That(detection.MethodName, Is.EqualTo("Parse"));

        // Step 2: convert
        var result = await _outParamEngine.ConvertOutParamsToValueTupleAsync(detection.FilePath, detection.MethodName);
        Assert.That(result.Success, Is.True, $"Conversion failed: {result.Message}");
    }
}
